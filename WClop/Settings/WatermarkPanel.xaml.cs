using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using WClop.Core.Settings;

namespace WClop.Settings
{
    /// <summary>
    /// Settings → Watermark: the default image, position, opacity, size and margin used by the drop zone's
    /// Watermark option and by <c>watermark</c> steps that don't set their own, with a live preview.
    /// </summary>
    public partial class WatermarkPanel : UserControl
    {
        /// <summary>The preview stands for a picture this wide, so the margin looks right.</summary>
        private const double PreviewRepresentsWidth = 1920;

        private AppSettings? _settings;
        private bool _loading;

        public WatermarkPanel()
        {
            InitializeComponent();
            PositionBox.ItemsSource = new[]
            {
                new Choice<string>("bottomRight", "Bottom right"),
                new Choice<string>("bottomLeft", "Bottom left"),
                new Choice<string>("topRight", "Top right"),
                new Choice<string>("topLeft", "Top left"),
                new Choice<string>("center", "Centre"),
            };
        }

        /// <summary>Something changed that should be applied.</summary>
        public event Action? Changed;

        private PipelineSettings Watermark => _settings!.Pipelines;

        public void Load(AppSettings settings)
        {
            _settings = settings;
            _loading = true;
            try
            {
                ImageBox.Text = Watermark.DefaultWatermark is { Length: > 0 } image ? PortablePath.Expand(image) : "";
                PositionBox.SelectedValue = Watermark.WatermarkPosition;
                if (PositionBox.SelectedIndex < 0)
                    PositionBox.SelectedIndex = 0;
                OpacitySlider.Value = Math.Round(Math.Clamp(Watermark.WatermarkOpacity, 0.05, 1) * 100);
                ScaleSlider.Value = Math.Round(Math.Clamp(Watermark.WatermarkScale, 0.03, 0.6) * 100);
                MarginBox.Text = Watermark.WatermarkMargin.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            finally
            {
                _loading = false;
            }

            ShowValues();
            UpdatePreview();
        }

        private void Setting_Changed(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (_loading || _settings is null)
                return;
            Watermark.WatermarkPosition = PositionBox.SelectedValue as string ?? "bottomRight";
            Watermark.WatermarkOpacity = OpacitySlider.Value / 100;
            Watermark.WatermarkScale = ScaleSlider.Value / 100;
            ShowValues();
            UpdatePreview();
            Changed?.Invoke();
        }

        private void MarginBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_settings is null)
                return;
            if (int.TryParse(MarginBox.Text.Trim(), out var margin) && margin is >= 0 and <= 2000)
            {
                Watermark.WatermarkMargin = margin;
                UpdatePreview();
                Changed?.Invoke();
            }

            MarginBox.Text = Watermark.WatermarkMargin.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private void ImageBox_LostFocus(object sender, RoutedEventArgs e) => SetImage(ImageBox.Text.Trim().Trim('"'));

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose the watermark image",
                Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|All files|*.*",
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            {
                ImageBox.Text = dialog.FileName;
                SetImage(dialog.FileName);
            }
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            ImageBox.Text = "";
            SetImage("");
        }

        private void SetImage(string path)
        {
            if (_settings is null)
                return;
            var value = path.Length == 0 ? null : PortablePath.Contract(path);
            if (value != Watermark.DefaultWatermark)
            {
                Watermark.DefaultWatermark = value;
                Changed?.Invoke();
            }

            UpdatePreview();
        }

        private void ShowValues()
        {
            OpacityText.Text = $"{OpacitySlider.Value:0}%";
            ScaleText.Text = $"{ScaleSlider.Value:0}%";
        }

        /// <summary>Draws the watermark on the sample background where, and how big, it would be.</summary>
        private void UpdatePreview()
        {
            var path = ImageBox.Text.Trim().Trim('"');
            BitmapImage? image = null;
            if (path.Length > 0 && File.Exists(path))
            {
                try
                {
                    image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad; // don't hold the file open
                    image.UriSource = new Uri(path);
                    image.EndInit();
                    image.Freeze();
                }
                catch (Exception e) when (e is NotSupportedException or IOException or ArgumentException or InvalidOperationException)
                {
                    image = null;
                }
            }

            ImageHint.Text = path.Length > 0 && image is null
                ? "That image couldn't be opened (missing, or a format Windows can't show)."
                : "A PNG with a transparent background works best: your logo, signature or handle.";
            PreviewEmpty.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
            PreviewImage.Source = image;
            if (image is null)
                return;

            var frameWidth = PreviewFrame.Width;
            var margin = Watermark.WatermarkMargin * frameWidth / PreviewRepresentsWidth;
            PreviewImage.Width = frameWidth * ScaleSlider.Value / 100;
            PreviewImage.Opacity = OpacitySlider.Value / 100;
            (PreviewImage.HorizontalAlignment, PreviewImage.VerticalAlignment) = (PositionBox.SelectedValue as string) switch
            {
                "bottomLeft" => (HorizontalAlignment.Left, VerticalAlignment.Bottom),
                "topRight" => (HorizontalAlignment.Right, VerticalAlignment.Top),
                "topLeft" => (HorizontalAlignment.Left, VerticalAlignment.Top),
                "center" => (HorizontalAlignment.Center, VerticalAlignment.Center),
                _ => (HorizontalAlignment.Right, VerticalAlignment.Bottom),
            };
            PreviewImage.Margin = new Thickness(margin);
        }
    }
}
