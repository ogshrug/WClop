using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using WClop.Core.Batch;
using WClop.Core.DropZone;
using WClop.Core.Logging;
using WClop.Core.Optimisation;
using MessageBox = System.Windows.MessageBox;

namespace WClop.Batch
{
    /// <summary>
    /// Many files at once (project.md §18.4): pick a preset, start, watch per-file progress, stop, or restore
    /// everything from the batch backup. Opened by dropping a folder or lots of files, or from the tray.
    /// </summary>
    public partial class BatchWindow : Window
    {
        private readonly FileOptimisationService _service;
        private readonly IReadOnlyList<string> _dropped;
        private readonly DispatcherTimer _refresh;
        private IReadOnlyList<BatchItem> _items = [];
        private BatchJob? _job;
        private bool _running;

        internal BatchWindow(FileOptimisationService service, IReadOnlyList<string> dropped, DropPreset preset, bool keepOriginals)
        {
            _service = service;
            _dropped = dropped;
            InitializeComponent();

            PresetBox.ItemsSource = DropPresets.All;
            PresetBox.SelectedItem = preset;
            KeepOriginalsBox.IsChecked = keepOriginals;
            // UI updates are coalesced: rows update themselves, the totals every quarter second.
            _refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => UpdateTotals(), Dispatcher);

            Loaded += async (_, _) => await CollectAsync();
            Closing += (_, e) =>
            {
                if (_running && MessageBox.Show(this, "Stop the batch? Files already done stay optimised.", "WClop",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }

                _job?.Stop();
                _refresh.Stop();
            };
        }

        private async Task CollectAsync()
        {
            StartButton.IsEnabled = false;
            Heading.Text = "Collecting files…";
            var subfolders = SubfoldersBox.IsChecked == true;
            _items = await Task.Run(() => BatchJob.Collect(_dropped, subfolders));
            Files.ItemsSource = _items;
            StartButton.IsEnabled = _items.Count > 0;
            UpdateTotals();
        }

        private async void Subfolders_Click(object sender, RoutedEventArgs e)
        {
            if (!_running && _job is null)
                await CollectAsync();
        }

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            var options = new BatchOptions
            {
                Preset = (DropPreset?)PresetBox.SelectedItem ?? DropPresets.All[0],
                KeepOriginals = KeepOriginalsBox.IsChecked == true,
            };

            _job = new BatchJob(_service, _items);
            _running = true;
            SetControls(running: true);
            _refresh.Start();
            try
            {
                Footer.Text = "Backing up originals…";
                await _job.BackUpAsync(done => Dispatcher.BeginInvoke(() => Overall.Value = (double)done / _items.Count));
                Footer.Text = "Optimising…";
                Overall.Value = 0;
                await _job.RunAsync(options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("Batch: couldn't back up the originals", ex);
                MessageBox.Show(this, $"The originals couldn't be backed up, so nothing was changed.\n\n{ex.Message}", "WClop",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _running = false;
                Overall.Value = 1;
                _refresh.Stop();
                UpdateTotals();
                SetControls(running: false);
            }
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            _job?.Stop();
            StopButton.IsEnabled = false;
            Footer.Text = "Stopping after the files in progress…";
        }

        private async void Restore_Click(object sender, RoutedEventArgs e)
        {
            if (_job is null || MessageBox.Show(this,
                    "Put every original back from the batch backup? Optimised files and copies from this batch are replaced or removed.",
                    "WClop", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            RestoreButton.IsEnabled = false;
            var restored = await _job.RestoreAllAsync();
            Footer.Text = $"Restored {restored} of {_items.Count} originals.";
            UpdateTotals();
        }

        private void Backups_Click(object sender, RoutedEventArgs e)
        {
            if (_job is not null && Directory.Exists(_job.BackupFolder))
                Process.Start(new ProcessStartInfo(_job.BackupFolder) { UseShellExecute = true });
        }

        private void SetControls(bool running)
        {
            PresetBox.IsEnabled = KeepOriginalsBox.IsEnabled = SubfoldersBox.IsEnabled = StartButton.IsEnabled = !running && _job is null;
            StopButton.IsEnabled = running;
            RestoreButton.IsEnabled = !running && _job is not null;
            BackupsButton.IsEnabled = _job is not null;
        }

        private void UpdateTotals()
        {
            var total = _items.Sum(i => i.OldSize);
            var finished = _items.Count(i => i.State is not (BatchItemState.Waiting or BatchItemState.Working));
            var done = _items.Where(i => i.State == BatchItemState.Done).ToList();
            var saved = done.Sum(i => i.Saved);

            Heading.Text = _items.Count == 0
                ? "No images, videos, PDFs or audio found"
                : $"{_items.Count} files · {OptimisationJob.FormatBytes(total)}";
            Summary.Text = _job is null
                ? "Pick a compression preset and press Start. Every original is backed up first."
                : $"{finished} of {_items.Count} finished · {done.Count} optimised · saved {OptimisationJob.FormatBytes(saved)}" +
                  (total > 0 ? $" ({(double)saved / total:P0})" : "");

            if (_job is not null && !_running && Footer.Text is "Optimising…" or "Stopping after the files in progress…")
                Footer.Text = $"Finished. Originals are kept in the batch backup until you delete it.";
            if (_running && _job is not null && Footer.Text == "Optimising…")
            {
                var progress = _items.Sum(i => i.State == BatchItemState.Working ? i.Progress : i.State == BatchItemState.Waiting ? 0 : 1);
                Overall.Value = _items.Count == 0 ? 0 : progress / _items.Count;
            }
        }
    }
}
