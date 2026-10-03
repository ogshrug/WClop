using System.Diagnostics;
using System.IO;
using WClop.Clipboard;
using WClop.Core.Compression;
using WClop.Core.Hotkeys;
using WClop.Core.Images;
using WClop.Core.Logging;
using WClop.Core.Optimisation;
using WClop.Results;

namespace WClop.Hotkeys
{
    /// <summary>
    /// What each hotkey does (project.md §17): with a current result (hovered, else newest) keys act on it;
    /// otherwise they act on whatever is on the clipboard.
    /// </summary>
    internal sealed class HotkeyActions(ResultsWindow results, ResultActions actions, ClipboardWatcher clipboard, TrayIcon tray)
    {
        public void Handle(HotkeyBinding binding)
        {
            try
            {
                var current = results.CurrentJob;
                switch (binding.Action)
                {
                    case HotkeyAction.OptimiseClipboard:
                        _ = clipboard.OptimiseNowAsync(new FileOptimisationRequest(), "Optimising the clipboard");
                        break;

                    case HotkeyAction.Aggressive:
                        if (current is not null)
                            actions.Adjust(current, CompressionModel.AggressiveImageFactor, null, "Optimising aggressively");
                        else
                            _ = clipboard.OptimiseNowAsync(
                                new FileOptimisationRequest { Factor = CompressionModel.AggressiveImageFactor }, "Optimising aggressively");
                        break;

                    case HotkeyAction.StepDown when current?.Result is { Kind: Core.Media.MediaKind.Pdf } pdf:
                        // PDFs step through the DPI stops; always re-rendered from the original (§10.4).
                        var dpi = Core.Pdf.PdfAnalysis.NextLowerDpi(pdf.Dpi ?? Core.Pdf.PdfAnalysis.LosslessDpi);
                        actions.AdjustPdf(current, dpi);
                        break;

                    case HotkeyAction.StepDown when current?.Result is { Kind: Core.Media.MediaKind.Audio } audio:
                        // Audio steps the bitrate down (§11) by raising the compression factor.
                        var factor = Math.Min(100, (audio.Factor ?? 35) + 15);
                        actions.Adjust(current, factor, null, $"Lowering the bitrate (factor {factor})");
                        break;

                    case HotkeyAction.StepDown:
                        Scale(current, ImageResizer.NextDownscaleStep(current is null ? 1 : ResultActions.CurrentScale(current)));
                        break;

                    case HotkeyAction.ScaleTo:
                        Scale(current, binding.Digit / 10.0);
                        break;

                    case HotkeyAction.Restore:
                        if (current is not null)
                            _ = actions.RestoreAsync(current);
                        break;

                    case HotkeyAction.CyclePause:
                        tray.CycleState();
                        break;

                    case HotkeyAction.DismissNewest:
                        results.DismissNewest();
                        break;

                    case HotkeyAction.BringBack:
                        results.BringBackLast();
                        break;

                    case HotkeyAction.ClearAll:
                        results.ClearAll();
                        clipboard.ClearCollection();
                        break;

                    case HotkeyAction.SpeedUp:
                        // Always from the original (§9.4): each step re-encodes it at the new speed.
                        if (current?.Result is { Kind: Core.Media.MediaKind.Video or Core.Media.MediaKind.Audio })
                        {
                            var speed = ResultActions.NextSpeedStep(ResultActions.CurrentSpeed(current));
                            actions.Adjust(current, null, null, $"Speeding up to {speed:0.##}×", speed);
                        }
                        else
                        {
                            tray.ShowNotice("Speed-up works on a video or audio result: optimise or drop one first");
                        }

                        break;

                    case HotkeyAction.Preview:
                        if (current?.CurrentPath is { } path && File.Exists(path))
                            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                        break;
                }
            }
            catch (Exception e)
            {
                // A hotkey must never take the app down.
                Log.Error($"Hotkey {binding.Action} ({binding.Key}) failed", e);
            }
        }

        private void Scale(OptimisationJob? current, double scale)
        {
            var label = ResultActions.ScalingLabel(current, scale);
            if (current is not null)
                actions.Adjust(current, null, scale, label);
            else
                _ = clipboard.OptimiseNowAsync(new FileOptimisationRequest { Scale = scale }, label);
        }
    }
}
