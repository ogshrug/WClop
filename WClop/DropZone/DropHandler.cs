using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using WClop.Core;
using WClop.Core.Clipboard;
using WClop.Core.Compression;
using WClop.Core.DropZone;
using WClop.Core.Images;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Pipelines;
using WClop.Core.Settings;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using IDataObject = System.Windows.IDataObject;

namespace WClop.DropZone
{
    /// <summary>
    /// Turns whatever was dropped on the drop zone into optimisation jobs (project.md §18.1–18.2):
    /// files and folders, virtual files (Outlook attachments, browser images), image URLs and raw bitmaps.
    /// Anything that has to be read from the drag source is read during the drop, while the data is still valid.
    /// </summary>
    internal sealed class DropHandler(
        FileOptimisationService service, OptimisationManager manager, AppSettings settings, PipelineLibrary pipelines)
    {
        /// <summary>For pipelines' copyToClipboard step.</summary>
        public Func<string, string, Task>? CopyToClipboard { get; set; }

        private const string FileGroupDescriptor = "FileGroupDescriptorW";
        private const string FileContents = "FileContents";
        private const string UrlFormat = "UniformResourceLocatorW";

        /// <summary>A folder or many files were dropped: open the batch window for them (with the drop's preset and Alt state).</summary>
        public event Action<IReadOnlyList<string>, DropPreset, bool>? BatchRequested;

        /// <summary>Something went wrong or there was nothing usable; shown as a notification.</summary>
        public event Action<string>? Notice;

        /// <summary>Quick check for <c>DragEnter</c>: is there anything here WClop might be able to use?</summary>
        public static bool MightAccept(IDataObject data) =>
            data.GetDataPresent(DataFormats.FileDrop)
            || data.GetDataPresent(FileGroupDescriptor)
            || data.GetDataPresent(UrlFormat)
            || data.GetDataPresent(DataFormats.Bitmap)
            || data.GetDataPresent("PNG")
            || data.GetDataPresent(DataFormats.UnicodeText);

        /// <summary>
        /// Starts jobs for the drop. Alt keeps originals (a copy is saved next to them), Ctrl optimises aggressively.
        /// Returns how many jobs were started.
        /// </summary>
        public int Handle(IDataObject data, DragDropKeyStates keys, DropPreset? preset = null)
        {
            // A scrolled-to preset decides; with the default preset, Ctrl still means aggressive.
            preset ??= DropPresets.All[0];
            var aggressive = preset == DropPresets.All[0] && keys.HasFlag(DragDropKeyStates.ControlKey);
            var keepOriginal = keys.HasFlag(DragDropKeyStates.AltKey);
            var request = preset.Apply(new FileOptimisationRequest
            {
                Force = true, // the user asked for these explicitly
                Factor = aggressive ? CompressionModel.AggressiveImageFactor : null,
                Behaviour = keepOriginal ? OutputBehaviour.SameFolder : null,
            });
            if (preset != DropPresets.All[0])
                WClop.Core.Logging.Log.Info($"Drop zone: preset {preset.Name}");

            if (data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
                return StartFiles(paths, request, preset, keepOriginal);

            if (data.GetDataPresent(FileGroupDescriptor))
                return StartVirtualFiles(data, request, preset);

            var temporary = request with { Behaviour = OutputBehaviour.Temporary };

            if (ReadUrl(data) is { } url)
            {
                StartJob($"url:{url}", Path.GetFileName(url.AbsolutePath) is { Length: > 0 } name ? name : url.Host, null,
                    async cancellationToken =>
                    {
                        var downloaded = await new MediaDownloader(service.Paths).DownloadAsync(url, cancellationToken);
                        if (!DropInputs.OptimisableMedia.Contains(FileTypeSniffer.Detect(downloaded)))
                            throw new UnsupportedFormatException("The link isn't an image or video WClop can optimise");
                        return downloaded;
                    }, temporary, preset);
                return 1;
            }

            if (ReadImageData(data) is { } image)
            {
                var path = AppPaths.NewTempPath(service.Paths.Images, ".png");
                File.WriteAllBytes(path, image);
                StartJob($"dropped:{path}", "Dropped image", null, _ => Task.FromResult(path), temporary, preset);
                return 1;
            }

            Notice?.Invoke("Nothing WClop can optimise was dropped");
            return 0;
        }

        private int StartFiles(string[] paths, FileOptimisationRequest request, DropPreset preset, bool keepOriginal)
        {
            var files = DropInputs.ExpandMediaPaths(paths, DropInputs.OptimisableMedia);

            // A folder, or lots of files, goes to the batch window instead of a pile of result cards (§18.4).
            var batch = settings.Files.BatchModeForFolders && paths.Any(Directory.Exists)
                        || files.Count > settings.Files.BatchModeFileCountThreshold;
            if (batch && BatchRequested is not null && preset.Pipeline is null)
            {
                BatchRequested(paths, preset, keepOriginal);
                return files.Count;
            }
            if (files.Count == 0)
            {
                Notice?.Invoke("No images or videos WClop can optimise in what was dropped (PDF and audio come later)");
                return 0;
            }

            foreach (var file in files)
            {
                manager.Start(Path.GetFullPath(file), JobSource.DropZone, Path.GetFileName(file), file,
                    (job, cancellationToken) => OptimiseAsync(file, request, preset, job, cancellationToken),
                    preset.Pipeline is { } name ? $"Running {name}" : "Optimising");
            }

            return files.Count;
        }

        /// <summary>Virtual files must be read now: the drag source only serves them during the drop.</summary>
        private int StartVirtualFiles(IDataObject data, FileOptimisationRequest request, DropPreset preset)
        {
            var descriptor = data.GetData(FileGroupDescriptor) as MemoryStream;
            var files = descriptor is null ? [] : DropInputs.ParseFileGroupDescriptor(descriptor.ToArray());
            var started = 0;

            foreach (var file in files.Where(f => DropInputs.OptimisableMedia.Contains(FileFormats.FromExtension(f.Name))))
            {
                var bytes = ReadVirtualFile(data, file.Index);
                if (bytes is not { Length: > 0 })
                    continue;

                Directory.CreateDirectory(service.Paths.Downloads);
                var path = Path.Combine(service.Paths.Downloads,
                    $"{Path.GetFileNameWithoutExtension(file.Name)}-{Guid.NewGuid().ToString("N")[..6]}{Path.GetExtension(file.Name)}");
                File.WriteAllBytes(path, bytes);
                StartJob($"dropped:{path}", file.Name, null, _ => Task.FromResult(path),
                    request with { Behaviour = OutputBehaviour.Temporary }, preset);
                started++;
            }

            if (started == 0)
                Notice?.Invoke(files.Count == 0
                    ? "The dragged files couldn't be read"
                    : "No images or videos among the dragged files");
            return started;
        }

        /// <summary>A job whose input first has to be fetched (download, or a file just written from the drop).</summary>
        private void StartJob(
            string key, string name, string? sourcePath, Func<CancellationToken, Task<string>> input, FileOptimisationRequest request,
            DropPreset preset) =>
            manager.Start(key, JobSource.DropZone, name, sourcePath, async (job, cancellationToken) =>
            {
                var path = await input(cancellationToken);
                FileOptimisationResult? result = null;
                try
                {
                    return result = await OptimiseAsync(path, request, preset, job, cancellationToken);
                }
                finally
                {
                    // A backup of the input is kept for restore; the fetched copy isn't needed (unless a pipeline
                    // worked on it in place and it's the result).
                    if (!string.Equals(result?.OutputPath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            File.Delete(path);
                        }
                        catch (IOException)
                        {
                        }
                    }
                }
            });

        /// <summary>
        /// Optimises a dropped file, then runs the scrolled-to pipeline or the pipelines attached to the drop zone
        /// (skipping the optimisation when they all say so).
        /// </summary>
        private async Task<FileOptimisationResult> OptimiseAsync(
            string path, FileOptimisationRequest request, DropPreset preset, OptimisationJob job, CancellationToken cancellationToken)
        {
            IReadOnlyList<ResolvedPipeline> chosen = preset.Pipeline is { } name
                ? [pipelines.Resolve(name)]
                : pipelines.AttachedTo(PipelineTrigger.DropZone, path);
            if (chosen.Count == 0)
                return await service.OptimiseAsync(path, request, p => job.Progress = p, cancellationToken);

            FileOptimisationResult start;
            if (PipelineLibrary.SkipsOptimisation(chosen))
            {
                start = await service.DescribeAsync(path, cancellationToken);
            }
            else
            {
                try
                {
                    start = await service.OptimiseAsync(path, request, p => job.Progress = p, cancellationToken);
                }
                catch (NotSmallerException)
                {
                    start = await service.DescribeAsync(path, cancellationToken);
                }
            }

            var context = new PipelineContext
            {
                Origin = PipelineOrigin.DropZone,
                OnStatus = status => job.Status = status,
                OnProgress = progress => job.Progress = progress,
                CopyToClipboard = CopyToClipboard,
            };
            var (result, _) = await pipelines.RunAllAsync(chosen, start, context, cancellationToken);
            // A pipeline picked for this drop always shows what it did.
            return preset.Pipeline is null ? result : result with { HideResult = false };
        }

        private static Uri? ReadUrl(IDataObject data)
        {
            string? text = null;
            if (data.GetData(UrlFormat) is MemoryStream stream)
                text = Encoding.Unicode.GetString(stream.ToArray()).TrimEnd('\0');
            else if (data.GetData(DataFormats.UnicodeText) is string unicode)
                text = unicode;

            return ClipboardText.Classify(text) switch
            {
                TextTarget.WebUrl url => url.Url,
                _ => null,
            };
        }

        /// <summary>PNG bytes, a base64 image in dropped text, or a bitmap re-encoded as PNG.</summary>
        private static byte[]? ReadImageData(IDataObject data)
        {
            if (data.GetData("PNG") is MemoryStream png)
                return png.ToArray();

            if (data.GetData(DataFormats.UnicodeText) is string text && ClipboardText.Classify(text) is TextTarget.Base64Image image)
                return image.Format == FileFormat.Png ? image.Data : null;

            if (data.GetData(DataFormats.Bitmap) is System.Windows.Media.Imaging.BitmapSource bitmap)
            {
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = new MemoryStream();
                encoder.Save(output);
                return output.ToArray();
            }

            return null;
        }

        /// <summary>
        /// Reads <c>FileContents</c> for one virtual file. WPF's data object has no way to ask for a file index,
        /// so this goes through the COM <c>IDataObject</c> with <c>lindex</c> set (project.md §26.1).
        /// </summary>
        private static byte[]? ReadVirtualFile(IDataObject data, int index)
        {
            if (data is not ComDataObject com)
                return null;

            var format = new FORMATETC
            {
                cfFormat = (short)DataFormats.GetDataFormat(FileContents).Id,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = index,
                ptd = IntPtr.Zero,
                tymed = TYMED.TYMED_HGLOBAL | TYMED.TYMED_ISTREAM,
            };

            try
            {
                com.GetData(ref format, out var medium);
                try
                {
                    return medium.tymed switch
                    {
                        TYMED.TYMED_HGLOBAL => ReadHGlobal(medium.unionmember),
                        TYMED.TYMED_ISTREAM => ReadStream((IStream)Marshal.GetObjectForIUnknown(medium.unionmember)),
                        _ => null,
                    };
                }
                finally
                {
                    ReleaseStgMedium(ref medium);
                }
            }
            catch (Exception e) when (e is COMException or InvalidCastException)
            {
                WClop.Core.Logging.Log.Warn($"Drop: couldn't read virtual file {index}: {e.Message}");
                return null;
            }
        }

        private static byte[] ReadHGlobal(IntPtr handle)
        {
            var pointer = GlobalLock(handle);
            try
            {
                var bytes = new byte[(int)GlobalSize(handle)];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }

        private static byte[] ReadStream(IStream stream)
        {
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            var readPointer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                while (true)
                {
                    stream.Read(buffer, buffer.Length, readPointer);
                    var read = Marshal.ReadInt32(readPointer);
                    if (read <= 0)
                        break;
                    output.Write(buffer, 0, read);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(readPointer);
            }

            return output.ToArray();
        }

        [DllImport("ole32.dll")]
        private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern UIntPtr GlobalSize(IntPtr hMem);
    }
}
