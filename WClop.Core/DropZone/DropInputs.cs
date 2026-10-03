using System.Buffers.Binary;
using System.Text;
using WClop.Core.Media;
using WClop.Core.Settings;

namespace WClop.Core.DropZone;

/// <summary>A screen rectangle in physical pixels.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom);

/// <summary>A file offered by a drag source that isn't on disk yet (Outlook attachments, browser images).</summary>
public sealed record VirtualFile(int Index, string Name, long? Size);

/// <summary>Pure helpers for the drop zone (project.md §18, §26.1).</summary>
public static class DropInputs
{
    /// <summary>
    /// Images WClop takes: PNG / JPEG / GIF are optimised directly, animated WebP re-encoded, and the rest converted
    /// first (HEIC / WebP / AVIF / BMP → JPEG, TIFF → PNG, per settings).
    /// </summary>
    public static readonly IReadOnlySet<FileFormat> OptimisableImages = new HashSet<FileFormat>
    {
        FileFormat.Png, FileFormat.Jpeg, FileFormat.Gif, FileFormat.WebP, FileFormat.Avif, FileFormat.Heic,
        FileFormat.Bmp, FileFormat.Tiff,
    };

    /// <summary>Videos the video engine takes (all come out as H.264 MP4).</summary>
    public static readonly IReadOnlySet<FileFormat> OptimisableVideos =
        new HashSet<FileFormat> { FileFormat.Mp4, FileFormat.Mov, FileFormat.WebM, FileFormat.Mkv, FileFormat.Avi, FileFormat.Mpeg };

    public static readonly IReadOnlySet<FileFormat> OptimisableAudio = new HashSet<FileFormat>
    {
        FileFormat.Mp3, FileFormat.M4a, FileFormat.Aac, FileFormat.Wav, FileFormat.Flac, FileFormat.Ogg, FileFormat.Aiff,
    };

    public static readonly IReadOnlySet<FileFormat> OptimisableMedia =
        new HashSet<FileFormat>(OptimisableImages.Concat(OptimisableVideos).Concat(OptimisableAudio).Append(FileFormat.Pdf));

    /// <summary>
    /// Dropped paths → the image files to optimise: files by extension, folders by the images directly inside
    /// (not subfolders, like the watchers). Hidden files are skipped; order is kept and duplicates removed.
    /// </summary>
    public static IReadOnlyList<string> ExpandMediaPaths(IEnumerable<string> paths, IReadOnlySet<FileFormat> formats)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string file)
        {
            if (Path.GetFileName(file).StartsWith('.') || !formats.Contains(FileFormats.FromExtension(file)))
                return;
            if (seen.Add(Path.GetFullPath(file)))
                result.Add(file);
        }

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(path).Order(StringComparer.OrdinalIgnoreCase))
                        Consider(file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
            else if (File.Exists(path))
            {
                Consider(path);
            }
        }

        return result;
    }

    /// <summary>Parses a <c>FILEGROUPDESCRIPTORW</c>: a count followed by 592-byte <c>FILEDESCRIPTORW</c> records.</summary>
    public static IReadOnlyList<VirtualFile> ParseFileGroupDescriptor(ReadOnlySpan<byte> data)
    {
        const int recordSize = 592;
        const int nameOffset = 72;
        const uint fdAttributes = 0x04;
        const uint fdFileSize = 0x40;
        const uint directoryAttribute = 0x10;

        var files = new List<VirtualFile>();
        if (data.Length < 4)
            return files;

        var count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data), (uint)((data.Length - 4) / recordSize));
        for (var i = 0; i < count; i++)
        {
            var record = data.Slice(4 + i * recordSize, recordSize);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(record);
            var attributes = BinaryPrimitives.ReadUInt32LittleEndian(record[36..]);
            if ((flags & fdAttributes) != 0 && (attributes & directoryAttribute) != 0)
                continue;

            long? size = (flags & fdFileSize) != 0
                ? ((long)BinaryPrimitives.ReadUInt32LittleEndian(record[64..]) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(record[68..])
                : null;

            var nameBytes = record.Slice(nameOffset, 520);
            var name = Encoding.Unicode.GetString(nameBytes);
            var end = name.IndexOf('\0');
            name = end >= 0 ? name[..end] : name;
            if (name.Length > 0)
                files.Add(new VirtualFile(i, Path.GetFileName(name), size));
        }

        return files;
    }
}

public enum ScreenEdge
{
    Left,
    Right,
    Top,
    Bottom,
}

/// <summary>
/// Where the drop-zone tab sits: on a screen edge, at a position along it (0–100 %, measured to the tab's centre).
/// Rectangles here are in whatever unit the caller uses consistently (pixels or DIPs).
/// </summary>
public static class DropZoneGeometry
{
    public static bool IsVertical(ScreenEdge edge) => edge is ScreenEdge.Left or ScreenEdge.Right;

    /// <summary>
    /// The rectangle for a tab <paramref name="length"/> long (along the edge) and <paramref name="depth"/> deep
    /// (out from the edge), kept fully on screen.
    /// </summary>
    public static ScreenRect Place(ScreenRect area, ScreenEdge edge, double percent, int length, int depth)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (IsVertical(edge))
        {
            var span = area.Bottom - area.Top;
            var top = Math.Clamp((int)Math.Round(area.Top + span * percent / 100 - length / 2.0), area.Top, Math.Max(area.Top, area.Bottom - length));
            var left = edge == ScreenEdge.Left ? area.Left : area.Right - depth;
            return new ScreenRect(left, top, left + depth, top + length);
        }
        else
        {
            var span = area.Right - area.Left;
            var left = Math.Clamp((int)Math.Round(area.Left + span * percent / 100 - length / 2.0), area.Left, Math.Max(area.Left, area.Right - length));
            var top = edge == ScreenEdge.Top ? area.Top : area.Bottom - depth;
            return new ScreenRect(left, top, left + length, top + depth);
        }
    }

    /// <summary>The edge closest to a point, and the point's position along it (for dragging the tab around).</summary>
    public static (ScreenEdge Edge, double Percent) Nearest(int x, int y, ScreenRect area)
    {
        var distances = new (ScreenEdge Edge, int Distance)[]
        {
            (ScreenEdge.Left, Math.Abs(x - area.Left)),
            (ScreenEdge.Right, Math.Abs(area.Right - x)),
            (ScreenEdge.Top, Math.Abs(y - area.Top)),
            (ScreenEdge.Bottom, Math.Abs(area.Bottom - y)),
        };
        var edge = distances.MinBy(d => d.Distance).Edge;
        var percent = IsVertical(edge)
            ? (y - area.Top) * 100.0 / Math.Max(1, area.Bottom - area.Top)
            : (x - area.Left) * 100.0 / Math.Max(1, area.Right - area.Left);
        return (edge, Math.Round(Math.Clamp(percent, 0, 100), 1));
    }

    /// <summary>
    /// A <paramref name="width"/> × <paramref name="height"/> zone centred on the cursor (the modifier-tap drop zone),
    /// moved as little as needed to stay fully inside <paramref name="area"/>.
    /// </summary>
    public static ScreenRect AtCursor(int x, int y, int width, int height, ScreenRect area)
    {
        var left = Math.Clamp(x - width / 2, area.Left, Math.Max(area.Left, area.Right - width));
        var top = Math.Clamp(y - height / 2, area.Top, Math.Max(area.Top, area.Bottom - height));
        return new ScreenRect(left, top, left + width, top + height);
    }

    /// <summary>Whether (x, y) is within <paramref name="reach"/> of a rectangle.</summary>
    public static bool IsNear(int x, int y, ScreenRect rect, int reach)
    {
        var dx = Math.Max(Math.Max(rect.Left - x, 0), x - rect.Right);
        var dy = Math.Max(Math.Max(rect.Top - y, 0), y - rect.Bottom);
        return dx * dx + dy * dy <= reach * reach;
    }
}
