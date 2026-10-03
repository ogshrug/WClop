using System.Globalization;
using System.Text.RegularExpressions;
using WClop.Core.Cropping;
using WClop.Core.DropZone;
using WClop.Core.Media;
using WClop.Core.Optimisation;
using WClop.Core.Settings;

namespace WClop.Core.Ipc;

/// <summary>
/// Turns an <see cref="OptimiseCommand"/>'s options into a request: the preset first, then explicit values on top.
/// Shared by the app (requests over the pipe) and the CLI (when the app isn't running).
/// </summary>
public sealed class OptimisationRequestBuilder
{
    private readonly OptimiseCommand _command;

    public OptimisationRequestBuilder(OptimiseCommand command)
    {
        _command = command;
        if (command.Preset is { } name)
        {
            var preset = DropPresets.All.FirstOrDefault(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (preset is null)
                Error = $"Unknown preset '{name}'. Presets: {string.Join(", ", DropPresets.All.Select(p => p.Name))}";
            else
                Preset = preset;
        }

        if (command.ConvertTo is { } extension)
        {
            var format = FileFormats.FromExtension("x." + extension.Trim().TrimStart('.'));
            if (format == FileFormat.Unknown)
                Error = $"Unknown format '{extension}'";
            else
                ConvertTo = format;
        }

        if (command.CropSize is not null || command.CropAspect is not null)
        {
            Crop = CropSpec.Parse(command.CropSize, command.CropAspect, command.SmartCrop, out var cropError);
            if (cropError is not null)
                Error = cropError;
        }

        if (command.Factor is < 1 or > 100)
            Error = "The factor must be between 1 and 100";
        if (command.Scale is <= 0 or > 1)
            Error = "The scale must be between 0 and 1 (e.g. 0.5 for half size)";
        if (command.TargetBytes is <= 0)
            Error = "The target size must be more than 0";
    }

    public DropPreset Preset { get; } = DropPresets.All[0];
    public FileFormat? ConvertTo { get; }

    /// <summary>Crop instead of optimising (see <see cref="FileOptimisationService.CropAsync"/>).</summary>
    public CropSpec? Crop { get; }
    public string? Error { get; }

    /// <summary>Whether anything beyond "optimise with the settings" was asked for.</summary>
    public bool HasOptions => _command.Preset is not null || _command.Factor is not null || _command.Scale is not null
                              || _command.TargetBytes is not null || _command.ConvertTo is not null
                              || _command.KeepOriginals || _command.Output is not null || _command.AllowLarger
                              || _command.Pipeline is not null || Crop is not null;

    public FileOptimisationRequest Build()
    {
        var request = Preset.Apply(new FileOptimisationRequest
        {
            // Asked for explicitly, so optimise even files marked as already done.
            Force = true,
            AllowLarger = _command.AllowLarger,
            Behaviour = _command.Output ?? (_command.KeepOriginals ? OutputBehaviour.SameFolder : null),
        });
        return request with
        {
            Factor = _command.Factor ?? request.Factor,
            Scale = _command.Scale ?? request.Scale,
            TargetBytes = _command.TargetBytes ?? request.TargetBytes,
        };
    }
}

/// <summary>Sizes as people type them: "10MB", "10 mb", "1.5G", "500k", "2048".</summary>
public static partial class SizeText
{
    public static bool TryParse(string text, out long bytes)
    {
        bytes = 0;
        var match = SizePattern().Match(text.Trim());
        if (!match.Success
            || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return false;

        var multiplier = match.Groups[2].Value.ToUpperInvariant() switch
        {
            "" or "B" => 1L,
            "K" or "KB" or "KIB" => 1024L,
            "M" or "MB" or "MIB" => 1024L * 1024,
            "G" or "GB" or "GIB" => 1024L * 1024 * 1024,
            _ => 0,
        };
        bytes = (long)Math.Round(number * multiplier);
        return multiplier > 0 && bytes > 0;
    }

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*([a-zA-Z]*)$")]
    private static partial Regex SizePattern();
}
