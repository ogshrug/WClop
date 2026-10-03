using WClop.Core.Cropping;
using WClop.Core.Images;
using WClop.Core.Media;
using WClop.Core.Placement;
using WClop.Core.Settings;
using WClop.Core.Watching;

namespace WClop.Core.Optimisation;

public sealed partial class FileOptimisationService
{
    /// <summary>
    /// Crops a result from its original backup (project.md §8.9: rect crops start from the pristine backup, so cropping
    /// twice doesn't compound), optimises the cropped image, and puts it where the result is: replacing it in place by
    /// default, staying in the working folder for clipboard results, or as <paramref name="behaviour"/> says (with
    /// <c>%f-cropped</c> next to it for "same folder"). Restoring brings the uncropped original back.
    /// </summary>
    public async Task<FileOptimisationResult> CropAsync(
        FileOptimisationResult previous, CropSpec crop, OutputBehaviour? behaviour = null,
        Action<double>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(previous.BackupPath))
            throw new FileNotFoundException("The backup of the original is gone (cleaned up?)", previous.BackupPath);

        var cropped = await Cropper.CropAsync(previous.BackupPath, crop, cancellationToken).ConfigureAwait(false);
        if (cropped is null)
        {
            var size = await Cropper.ReadSizeAsync(previous.BackupPath, previous.Kind, cancellationToken).ConfigureAwait(false);
            throw new NothingToCropException($"Already {(size is null ? "that size" : $"{size.Width}×{size.Height}")}: nothing to crop");
        }

        onProgress?.Invoke(0.5);
        var output = cropped.Path;
        if (previous.Kind == MediaKind.Image)
        {
            // The crop was written at high quality; this is the one lossy step.
            try
            {
                var optimised = await _images.OptimiseAsync(cropped.Path, new ImageOptimiseOptions
                {
                    Factor = previous.Factor ?? _settings.Compression.ImageFactor,
                    AllowLarger = true,
                    StripMetadata = _settings.Files.StripMetadata,
                    PreserveColorMetadata = _settings.Files.PreserveColorMetadata,
                    MetadataSource = previous.BackupPath,
                }, cancellationToken).ConfigureAwait(false);
                File.Delete(cropped.Path);
                output = optimised.Path;
            }
            catch (OptimisationException)
            {
                // Keep the plain crop.
            }
        }

        string finalPath;
        if (behaviour is null && WatchFilters.IsInside(previous.OutputPath, Paths.WorkDir))
        {
            finalPath = output;
        }
        else
        {
            var placement = previous.Kind == MediaKind.Video ? _settings.Files.Videos : _settings.Files.Images;
            var how = behaviour ?? OutputBehaviour.InPlace;
            finalPath = _placer.Place(new PlacementRequest
            {
                OutputPath = output,
                OriginalPath = previous.OutputPath,
                Behaviour = how,
                Template = how == OutputBehaviour.SpecificFolder ? placement.SpecificFolderTemplate : "%f-cropped",
                PreserveTimes = _settings.Files.PreserveDates ? previous.OriginalTimes : null,
            });
        }

        onProgress?.Invoke(1);
        return previous with
        {
            OutputPath = finalPath,
            OutputFormat = FileTypeSniffer.Detect(finalPath),
            NewSize = new FileInfo(finalPath).Length,
            FromCache = false,
            Scale = 1,
            Speed = 1,
            IsConversion = true,
            Crop = cropped.Size + (cropped.Smart ? " (smart)" : ""),
            TargetBytes = null,
            MissedTarget = false,
        };
    }
}
