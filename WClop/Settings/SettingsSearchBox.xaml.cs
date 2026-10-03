using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WClop.Core.Settings;

namespace WClop.Settings
{
    /// <summary>
    /// Search across every Settings tab (Ctrl+F). Typing lists the closest matching settings; choosing one switches to
    /// its tab, scrolls it into view, focuses it and briefly highlights it. The index is rebuilt from the window's
    /// controls each time the box is focused, so it always matches what the tabs show.
    /// </summary>
    public partial class SettingsSearchBox : UserControl
    {
        private static readonly TimeSpan HighlightFor = TimeSpan.FromSeconds(1.6);

        private TabControl? _tabs;
        private SettingsSearchIndex? _index;

        public SettingsSearchBox()
        {
            InitializeComponent();
        }

        /// <summary>One line in the results list.</summary>
        private sealed record Hit(string Label, string Location, SettingsSearchIndex.Target Target);

        /// <summary>Connects the box to the tabs it searches and the window it sits in.</summary>
        internal void Attach(TabControl tabs, Window window)
        {
            _tabs = tabs;
            window.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    QueryBox.Focus();
                    QueryBox.SelectAll();
                    e.Handled = true;
                }
            };
            // The results are a popup, which doesn't follow the window.
            window.LocationChanged += (_, _) => ResultsPopup.IsOpen = false;
            window.SizeChanged += (_, _) => ResultsPopup.IsOpen = false;
            window.Deactivated += (_, _) => ResultsPopup.IsOpen = false;
        }

        private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            Placeholder.Visibility = QueryBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Search();
        }

        private void Search()
        {
            if (_tabs is null || string.IsNullOrWhiteSpace(QueryBox.Text))
            {
                ResultsList.ItemsSource = null;
                ResultsPopup.IsOpen = false;
                return;
            }

            _index ??= SettingsSearchIndex.Build(_tabs);
            // Only what can be seen: hidden controls (an update button with no update) aren't offered.
            var shown = Enumerable.Range(0, _index.Entries.Count)
                .Where(i => _index.Targets[i].Element is not { } element || SettingsSearchIndex.IsShown(element))
                .ToList();
            var results = SettingsSearch.Find(shown.Select(i => _index.Entries[i]).ToList(), QueryBox.Text);

            var hits = results.Select(r => new Hit(r.Entry.Label, r.Entry.Location, _index.Targets[shown[r.Index]])).ToList();
            ResultsList.ItemsSource = hits;
            ResultsList.Visibility = hits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            NoResults.Visibility = hits.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            ResultsList.SelectedIndex = hits.Count > 0 ? 0 : -1;
            UpdatePopup();
        }

        private void Focus_Changed(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender == QueryBox && e.RoutedEvent == GotKeyboardFocusEvent)
                _index = null; // settings may have appeared or been hidden since the last search

            // Focus passes through nothing on its way between the box and the list; look once it has settled.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, UpdatePopup);
        }

        private void UpdatePopup() =>
            ResultsPopup.IsOpen = !string.IsNullOrWhiteSpace(QueryBox.Text)
                                  && (QueryBox.IsKeyboardFocusWithin || ResultsList.IsKeyboardFocusWithin);

        private void QueryBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down when ResultsList.Items.Count > 0:
                    ResultsList.SelectedIndex = Math.Max(ResultsList.SelectedIndex, 0);
                    FocusSelected();
                    e.Handled = true;
                    break;
                case Key.Enter when ResultsList.SelectedItem is Hit hit:
                    Reveal(hit);
                    e.Handled = true;
                    break;
                case Key.Escape when QueryBox.Text.Length > 0:
                    QueryBox.Clear();
                    e.Handled = true;
                    break;
            }
        }

        private void ResultsList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter when ResultsList.SelectedItem is Hit hit:
                    Reveal(hit);
                    e.Handled = true;
                    break;
                case Key.Escape:
                case Key.Up when ResultsList.SelectedIndex <= 0:
                    QueryBox.Focus();
                    e.Handled = true;
                    break;
            }
        }

        private void ResultsList_Click(object sender, MouseButtonEventArgs e)
        {
            if (ItemsControl.ContainerFromElement(ResultsList, (DependencyObject)e.OriginalSource) is ListBoxItem { Content: Hit hit })
                Reveal(hit);
        }

        private void FocusSelected()
        {
            ResultsList.UpdateLayout();
            if (ResultsList.ItemContainerGenerator.ContainerFromIndex(ResultsList.SelectedIndex) is ListBoxItem item)
                item.Focus();
            else
                ResultsList.Focus();
        }

        /// <summary>Switch to the setting's tab, bring it into view, focus it and highlight it.</summary>
        private void Reveal(Hit hit)
        {
            if (_tabs is null)
                return;

            var target = hit.Target;
            ResultsPopup.IsOpen = false;
            _tabs.SelectedItem = target.Tab;
            if (target.Element is not { } element)
            {
                target.Tab.Focus();
                return;
            }

            // A tab that wasn't showing has to be laid out before anything on it can be scrolled to.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                var control = target.IsLabel ? SettingsSearchIndex.Partner(element) : element as Control;
                element.BringIntoView();
                Highlight(element);
                if (control is not null && control != element)
                    Highlight(control);

                if (control is { Focusable: true, IsEnabled: true, IsVisible: true })
                    control.Focus();
                else
                    target.Tab.Focus();
            });
        }

        private void Highlight(FrameworkElement element)
        {
            if (AdornerLayer.GetAdornerLayer(element) is not { } layer)
                return;

            var accent = TryFindResource("AccentFillColorDefaultBrush") is SolidColorBrush brush ? brush.Color : SystemColors.HighlightColor;
            var adorner = new SearchHighlightAdorner(element, accent);
            layer.Add(adorner);
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(600)) { BeginTime = HighlightFor };
            fade.Completed += (_, _) => layer.Remove(adorner);
            adorner.BeginAnimation(OpacityProperty, fade);
        }
    }
}
