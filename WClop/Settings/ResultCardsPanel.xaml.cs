using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using WClop.Core.Settings;

namespace WClop.Settings
{
    /// <summary>
    /// Settings → Results → Cards and Edit with: the format bar, when the compact list takes over, and the app
    /// "Edit with…" opens per kind of file.
    /// </summary>
    public partial class ResultCardsPanel : UserControl
    {
        private ResultCardSettings? _settings;
        private bool _loading;

        public ResultCardsPanel() => InitializeComponent();

        /// <summary>Something changed that should be applied.</summary>
        public event Action? Changed;

        public void Load(AppSettings settings)
        {
            _settings = settings.ResultCards;
            _loading = true;
            try
            {
                FormatBarBox.IsChecked = _settings.ShowFormatBar;
                ThresholdBox.Text = _settings.CompactListThreshold.ToString(CultureInfo.InvariantCulture);
                EditorsGrid.Children.Clear();
                AddEditorRow(0, "Images", () => _settings.EditImagesWith, v => _settings.EditImagesWith = v);
                AddEditorRow(1, "Videos", () => _settings.EditVideosWith, v => _settings.EditVideosWith = v);
                AddEditorRow(2, "PDFs", () => _settings.EditPdfsWith, v => _settings.EditPdfsWith = v);
                AddEditorRow(3, "Audio", () => _settings.EditAudioWith, v => _settings.EditAudioWith = v);
            }
            finally
            {
                _loading = false;
            }
        }

        private void AddEditorRow(int row, string kind, Func<string?> get, Action<string?> set)
        {
            var label = new TextBlock { Text = kind, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
            var box = new TextBox
            {
                Text = get() is { Length: > 0 } app ? PortablePath.Expand(app) : "",
                Margin = new Thickness(0, 3, 0, 3),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            System.Windows.Automation.AutomationProperties.SetName(box, $"Edit {kind.ToLowerInvariant()} with");
            var browse = new Button { Content = "Choose…", Margin = new Thickness(8, 3, 0, 3), Padding = new Thickness(12, 2, 12, 2) };
            var clear = new Button { Content = "Clear", Margin = new Thickness(8, 3, 0, 3), Padding = new Thickness(12, 2, 12, 2) };

            void Store(string text)
            {
                var value = text.Trim().Trim('"') is { Length: > 0 } path ? PortablePath.Contract(path) : null;
                if (value == get())
                    return;
                set(value);
                if (!_loading)
                    Changed?.Invoke();
            }

            box.LostFocus += (_, _) => Store(box.Text);
            browse.Click += (_, _) =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = $"Choose the app to edit {kind.ToLowerInvariant()} with",
                    Filter = "Programs|*.exe;*.bat;*.cmd|All files|*.*",
                };
                if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                    return;
                box.Text = dialog.FileName;
                Store(dialog.FileName);
            };
            clear.Click += (_, _) =>
            {
                box.Text = "";
                Store("");
            };

            foreach (var (element, column) in new (UIElement, int)[] { (label, 0), (box, 1), (browse, 2), (clear, 3) })
            {
                Grid.SetRow(element, row);
                Grid.SetColumn(element, column);
                EditorsGrid.Children.Add(element);
            }
        }

        private void Setting_Changed(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (_loading || _settings is null)
                return;
            _settings.ShowFormatBar = FormatBarBox.IsChecked == true;
            Changed?.Invoke();
        }

        private void ThresholdBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_settings is null)
                return;
            if (int.TryParse(ThresholdBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var threshold)
                && threshold is >= 1 and <= 30 && threshold != _settings.CompactListThreshold)
            {
                _settings.CompactListThreshold = threshold;
                Changed?.Invoke();
            }

            ThresholdBox.Text = _settings.CompactListThreshold.ToString(CultureInfo.InvariantCulture);
        }
    }
}
