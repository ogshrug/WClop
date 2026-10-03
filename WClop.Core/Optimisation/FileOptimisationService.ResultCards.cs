using WClop.Core.Audio;
using WClop.Core.Images;
using WClop.Core.Media;
using WClop.Core.Processes;
using WClop.Core.Storage;

namespace WClop.Core.Optimisation;

/// <summary>File operations the result cards offer beyond optimising: renaming, and converting audio.</summary>
public sealed partial class FileOptimisationService
{
    /// <summary>Formats audio can be converted to from a card (the format bar) or <c>wclop optimise --to</c>.</summary>
    public static readonly IReadOnlyList<FileFormat> AudioConversionTargets = [FileFormat.Mp3, FileFormat.M4a, FileFormat.Ogg];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// The file name a typed name becomes (rename from a card): trimmed as Windows would, and keeping the file's
    /// extension unless the name already has one for the same format (<c>shot</c> → <c>shot.png</c>,
    /// <c>shot.PNG</c> stays). Throws <see cref="ArgumentException"/> with a message for the card if it can't be used.
    /// </summary>
    public static string RenamedFileName(string currentPath, string typed)
    {
        var name = typed.Trim().TrimEnd('.', ' ');
        if (name.Length == 0)
            throw new ArgumentException("Type a name for the file");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("A file name can't contain \\ / : * ? \" < > |");

        var format = FileFormats.FromExtension(currentPath);
        if (format == FileFormat.Unknown || FileFormats.FromExtension(name) != format)
            name += Path.GetExtension(currentPath);
        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(name)))
            throw new ArgumentException($"Windows doesn't allow a file called {Path.GetFileNameWithoutExtension(name)}");
        return name;
    }

    /// <summary>
    /// Renames a result's file in its folder (see <see cref="RenamedFileName"/>), keeping its marker so it isn't
    /// optimised again. Returns the new path; never replaces another file.
    /// </summary>
    public string RenameFile(string path, string newName)
    {
        var target = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, RenamedFileName(path, newName));
        if (string.Equals(target, path, StringComparison.Ordinal))
            return path;
        // A change of case only is still a rename of the same file.
        if (File.Exists(target) && !string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"There's already a file called {Path.GetFileName(target)} there");

        var status = _markers.Get(path);
        _recentWrites?.Register(path);
        _recentWrites?.Register(target);
        File.Move(path, target);
        _markers.Clear(path);
        if (status != MarkerStatus.None)
            _markers.Set(target, status);
        return target;
    }

    /// <summary>
    /// Re-encodes audio to MP3, AAC (M4A) or Opus (Ogg) at the compression factor's bitrate, never above the source's
    /// (§11), keeping cover art where the format can carry it. Output goes to the conversions folder.
    /// </summary>
    private async Task<string> ConvertAudioAsync(string input, FileFormat target, Action<double>? onProgress, CancellationToken cancellationToken)
    {
        if (!AudioConversionTargets.Contains(target))
            throw new UnsupportedFormatException($"Converting audio to {target} isn't supported");
        var info = await AudioInfo.ProbeAsync(Tools.Require(Tool.Ffprobe), input, cancellationToken).ConfigureAwait(false)
                   ?? throw new UnsupportedFormatException("Not an audio file ffmpeg can read");

        var bitrate = AudioBitrates.For(target, _settings.Compression.AudioFactor);
        if (!info.IsLossless)
            bitrate = AudioBitrates.Capped(target, bitrate, info.BitrateKbps);

        Directory.CreateDirectory(Paths.Conversions);
        var output = AppPaths.NewTempPath(Paths.Conversions, target.Extension());
        var seconds = info.Duration.TotalSeconds;
        var result = await ProcessRunner.RunAsync(Tools.Require(Tool.Ffmpeg), AudioOptimiser.Arguments(input, output, target, bitrate, info),
            new ProcessRunOptions
            {
                Timeout = TimeSpan.FromMinutes(Math.Max(5, seconds / 60)),
                OnStdErrLine = line => Video.VideoOptimiser.ReportProgress(line, seconds, onProgress),
            }, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || !File.Exists(output) || new FileInfo(output).Length == 0)
        {
            File.Delete(output);
            throw new ToolFailedException(result);
        }

        onProgress?.Invoke(1);
        return output;
    }
}
