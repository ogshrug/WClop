using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WClop.Core.Pipelines;
using WClop.Core.Settings;

namespace WClop.Settings
{
    /// <summary>
    /// Settings → Pipelines: edit saved pipelines with live checking, choose where they run automatically, and try
    /// one on a file. Edits go straight into the live settings; <see cref="Changed"/> asks the window to apply them.
    /// </summary>
    public partial class PipelinesPanel : UserControl
    {
        private static readonly (string Name, string Script, bool Skip)[] Examples =
        [
            ("Web images", "downscale(longEdge: 1920)\n-> convert(webp)", true),
            ("Screenshots by month", "if(regex: \"^screenshot\")\n-> move(to: \"~/Pictures/Screenshots/%y-%m/\")", false),
            ("Square thumbnail", "crop(aspectRatio: \"1:1\")\n-> downscale(width: 512)", true),
            ("Discord video", "if(type: video)\n-> removeAudio\n-> targetSize(10MB)", true),
            ("Copy path", "copyToClipboard(as: path)", false),
            ("No metadata", "stripExif", false),
            ("Watermark", "watermark(position: bottomRight, opacity: 80%)", false),
        ];

        private readonly ObservableCollection<SavedPipeline> _pipelines = [];
        private AppSettings? _settings;
        private bool _loading;

        public PipelinesPanel()
        {
            InitializeComponent();
            ReferenceBox.Text = PipelineCatalog.Prompt();
            PipelineList.ItemsSource = _pipelines;
        }

        /// <summary>Something changed that should be applied.</summary>
        public event Action? Changed;

        /// <summary>Runs a saved pipeline on a file (set by the app).</summary>
        public Action<string, string>? TryPipeline { get; set; }

        private PipelineSettings Pipelines => _settings!.Pipelines;
        private SavedPipeline? Selected => PipelineList.SelectedItem as SavedPipeline;

        public void Load(AppSettings settings)
        {
            _settings = settings;
            _pipelines.Clear();
            foreach (var pipeline in Pipelines.Saved)
                _pipelines.Add(pipeline);
            _loading = true;
            AssistantScriptsBox.IsChecked = Pipelines.AllowScriptsFromAssistants;
            _loading = false;
            PipelineList.SelectedIndex = _pipelines.Count > 0 ? 0 : -1;
            ShowSelected();
        }

        private void PipelineList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelected();

        private void ShowSelected()
        {
            _loading = true;
            try
            {
                var pipeline = Selected;
                Editor.IsEnabled = pipeline is not null;
                DeleteButton.IsEnabled = pipeline is not null;
                NameBox.Text = pipeline?.Name ?? "";
                ScriptBox.Text = pipeline?.Script ?? "";
                SkipBox.IsChecked = pipeline?.SkipOptimisation;
                HideBox.IsChecked = pipeline?.HideResult;
                DropZoneBox.IsChecked = pipeline?.ShowInDropZone;
                ResultsBox.IsChecked = pipeline?.ShowOnResults;

                var attached = pipeline is null ? [] : AttachmentsOf(pipeline.Name).ToList();
                ClipboardBox.IsChecked = attached.Any(a => a.Trigger == PipelineTrigger.Clipboard);
                DropAttachBox.IsChecked = attached.Any(a => a.Trigger == PipelineTrigger.DropZone);

                var folders = WatchedFolders()
                    .Select(f => new FolderChoice(f.Folder, f.Label,
                        attached.Any(a => a.Trigger == PipelineTrigger.Folder && string.Equals(a.Folder, f.Folder, StringComparison.OrdinalIgnoreCase))))
                    .ToList();
                FolderChecks.ItemsSource = folders;
                NoFoldersHint.Visibility = folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                var kinds = pipeline?.Kinds ?? [];
                KindImage.IsChecked = kinds.Contains("image");
                KindVideo.IsChecked = kinds.Contains("video");
                KindPdf.IsChecked = kinds.Contains("pdf");
                KindAudio.IsChecked = kinds.Contains("audio");
                Check();
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>Every watched folder, labelled with what it watches.</summary>
        private IEnumerable<(string Folder, string Label)> WatchedFolders()
        {
            var watching = _settings!.Watching;
            var all = new (WatcherSettings Watcher, string Kind)[]
            {
                (watching.Images, "images"), (watching.Videos, "videos"), (watching.Pdfs, "PDFs"), (watching.Audio, "audio"),
            };
            return all
                .SelectMany(w => w.Watcher.Folders.Select(f => (Folder: f, w.Kind)))
                .GroupBy(f => f.Folder, StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.Key, $"Files in {PortablePath.Expand(g.Key)} ({string.Join(", ", g.Select(x => x.Kind))})"));
        }

        private IEnumerable<PipelineAttachment> AttachmentsOf(string name) =>
            Pipelines.Attached.Where(a => a.Pipeline.Equals(name, StringComparison.OrdinalIgnoreCase));

        private void Check()
        {
            if (Selected is null)
            {
                CheckText.Text = "";
                return;
            }

            if (PipelineCatalog.TryCompile(ScriptBox.Text, out var compiled, out var error))
            {
                CheckText.Text = $"✓ {compiled!.Steps.Count} step{(compiled.Steps.Count == 1 ? "" : "s")}: {string.Join(" → ", compiled.Steps.Select(s => s.Name))}";
                CheckText.Foreground = (Brush)FindResource(SystemColors.GrayTextBrushKey);
                TryButton.IsEnabled = TryPipeline is not null;
            }
            else
            {
                CheckText.Text = error;
                CheckText.Foreground = Brushes.IndianRed;
                TryButton.IsEnabled = false;
            }
        }

        private void ScriptBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading || Selected is not { } pipeline)
                return;
            pipeline.Script = ScriptBox.Text;
            Check();
        }

        private void NameBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (Selected is not { } pipeline)
                return;
            var name = UniqueName(NameBox.Text.Trim(), pipeline);
            if (name == pipeline.Name)
            {
                NameBox.Text = name;
                return;
            }

            foreach (var attachment in AttachmentsOf(pipeline.Name))
                attachment.Pipeline = name;
            pipeline.Name = name;
            NameBox.Text = name;
            RefreshList(pipeline);
            Changed?.Invoke();
        }

        private string UniqueName(string wanted, SavedPipeline? except = null)
        {
            if (wanted.Length == 0)
                wanted = "Pipeline";
            var name = wanted;
            for (var i = 2; Pipelines.Saved.Any(p => p != except && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++)
                name = $"{wanted} {i}";
            return name;
        }

        private void RefreshList(SavedPipeline select)
        {
            _pipelines.Clear();
            foreach (var pipeline in Pipelines.Saved)
                _pipelines.Add(pipeline);
            PipelineList.SelectedItem = select;
        }

        private void Add(string name, string script, bool skip)
        {
            var pipeline = new SavedPipeline { Name = UniqueName(name), Script = script, SkipOptimisation = skip };
            Pipelines.Saved.Add(pipeline);
            RefreshList(pipeline);
            Changed?.Invoke();
            ScriptBox.Focus();
        }

        private void New_Click(object sender, RoutedEventArgs e) => Add("New pipeline", "optimise", false);

        private void Examples_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = (FrameworkElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var (name, script, skip) in Examples)
            {
                var item = new MenuItem { Header = $"{name}:  {script.Replace("\n", " ")}" };
                item.Click += (_, _) => Add(name, script, skip);
                menu.Items.Add(item);
            }

            menu.IsOpen = true;
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (Selected is not { } pipeline)
                return;
            Pipelines.Saved.Remove(pipeline);
            Pipelines.Attached.RemoveAll(a => a.Pipeline.Equals(pipeline.Name, StringComparison.OrdinalIgnoreCase));
            _pipelines.Remove(pipeline);
            PipelineList.SelectedIndex = _pipelines.Count > 0 ? 0 : -1;
            Changed?.Invoke();
        }

        private void Flag_Click(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;
            Pipelines.AllowScriptsFromAssistants = AssistantScriptsBox.IsChecked == true;
            if (Selected is { } pipeline)
            {
                pipeline.SkipOptimisation = SkipBox.IsChecked == true;
                pipeline.HideResult = HideBox.IsChecked == true;
                pipeline.ShowInDropZone = DropZoneBox.IsChecked == true;
                pipeline.ShowOnResults = ResultsBox.IsChecked == true;
            }

            Changed?.Invoke();
        }

        /// <summary>Rebuilds this pipeline's attachments from the checkboxes.</summary>
        private void Attach_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || Selected is not { } pipeline)
                return;

            // Kinds are the pipeline's own (Kinds_Changed); attachments made here apply to all of them.
            List<string> kinds = [];
            Pipelines.Attached.RemoveAll(a => a.Pipeline.Equals(pipeline.Name, StringComparison.OrdinalIgnoreCase));
            if (ClipboardBox.IsChecked == true)
                Pipelines.Attached.Add(new PipelineAttachment { Pipeline = pipeline.Name, Trigger = PipelineTrigger.Clipboard, Kinds = [.. kinds] });
            if (DropAttachBox.IsChecked == true)
                Pipelines.Attached.Add(new PipelineAttachment { Pipeline = pipeline.Name, Trigger = PipelineTrigger.DropZone, Kinds = [.. kinds] });
            foreach (var folder in (FolderChecks.ItemsSource as IEnumerable<FolderChoice> ?? []).Where(f => f.IsChecked))
            {
                Pipelines.Attached.Add(new PipelineAttachment
                {
                    Pipeline = pipeline.Name, Trigger = PipelineTrigger.Folder, Folder = folder.Folder, Kinds = [.. kinds],
                });
            }

            Changed?.Invoke();
        }

        private void Kinds_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || Selected is not { } pipeline)
                return;
            pipeline.Kinds = [];
            if (KindImage.IsChecked == true) pipeline.Kinds.Add("image");
            if (KindVideo.IsChecked == true) pipeline.Kinds.Add("video");
            if (KindPdf.IsChecked == true) pipeline.Kinds.Add("pdf");
            if (KindAudio.IsChecked == true) pipeline.Kinds.Add("audio");
            Changed?.Invoke();
        }

        private void Try_Click(object sender, RoutedEventArgs e)
        {
            if (Selected is not { } pipeline || TryPipeline is null)
                return;
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"Run {pipeline.Name} on…",
                Filter = "Images, videos, PDFs and audio|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.avif;*.heic;*.bmp;*.tif;*.tiff;*.mp4;*.mov;*.webm;*.mkv;*.avi;*.pdf;*.mp3;*.m4a;*.wav;*.flac;*.ogg|All files|*.*",
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true && File.Exists(dialog.FileName))
            {
                Changed?.Invoke();
                TryPipeline(pipeline.Name, dialog.FileName);
            }
        }

        /// <summary>A watched folder as a checkbox.</summary>
        private sealed class FolderChoice(string folder, string label, bool isChecked) : INotifyPropertyChanged
        {
            private bool _isChecked = isChecked;

            public string Folder { get; } = folder;
            public string Label { get; } = label;

            public bool IsChecked
            {
                get => _isChecked;
                set
                {
                    _isChecked = value;
                    OnPropertyChanged();
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            private void OnPropertyChanged([CallerMemberName] string? name = null) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
