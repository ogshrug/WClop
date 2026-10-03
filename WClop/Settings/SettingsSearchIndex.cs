using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using WClop.Core.Settings;

namespace WClop.Settings
{
    /// <summary>
    /// What the Settings search looks through, read from the window's own controls (the logical tree, which exists
    /// for every tab, not just the one showing): check boxes and buttons by their text, text blocks styled Label
    /// and Section, and Hint text, which is searched as part of the setting above it. Nothing is listed by hand, so
    /// a setting added to the XAML is found as soon as it's there.
    /// </summary>
    internal sealed class SettingsSearchIndex
    {
        private SettingsSearchIndex(IReadOnlyList<SettingsSearchEntry> entries, IReadOnlyList<Target> targets)
        {
            Entries = entries;
            Targets = targets;
        }

        public IReadOnlyList<SettingsSearchEntry> Entries { get; }

        /// <summary>Where each entry (same index) lives: its tab, and the element to scroll to and highlight.</summary>
        public IReadOnlyList<Target> Targets { get; }

        /// <summary>
        /// Where an entry lives. <paramref name="IsLabel"/> marks a Label text block, whose <see cref="Partner"/> is the
        /// control it names.
        /// </summary>
        public sealed record Target(TabItem Tab, FrameworkElement? Element, bool IsLabel = false);

        public static SettingsSearchIndex Build(TabControl tabs)
        {
            var entries = new List<SettingsSearchEntry>();
            var targets = new List<Target>();
            foreach (var tab in tabs.Items.OfType<TabItem>())
            {
                var tabName = tab.Header as string ?? "";
                entries.Add(new SettingsSearchEntry(tabName, tabName));
                targets.Add(new Target(tab, null));
                if (tab.Content is DependencyObject content)
                    new Walker(tab, tabName, entries, targets, entries.Count - 1).Visit(content);
            }

            return new SettingsSearchIndex(entries, targets);
        }

        /// <summary>
        /// Whether an element would be seen on its tab: hidden elements (and ones inside a hidden panel, such as
        /// the update button before an update is found) are left out of the results.
        /// </summary>
        public static bool IsShown(FrameworkElement element)
        {
            for (DependencyObject? node = element; node is not null and not TabItem; node = LogicalTreeHelper.GetParent(node))
            {
                if (node is UIElement { Visibility: not Visibility.Visible })
                    return false;
            }

            return true;
        }

        /// <summary>The control a Label text block names: the next control beside it, or the one in its grid row.</summary>
        public static Control? Partner(FrameworkElement label)
        {
            switch (label.Parent)
            {
                case Grid grid:
                    var row = Grid.GetRow(label);
                    return grid.Children.OfType<Control>().FirstOrDefault(c => Grid.GetRow(c) == row);
                case Panel panel:
                    var index = panel.Children.IndexOf(label);
                    return panel.Children.OfType<UIElement>().Skip(index + 1).OfType<Control>().FirstOrDefault();
                default:
                    return null;
            }
        }

        private sealed class Walker(TabItem tab, string tabName, List<SettingsSearchEntry> entries, List<Target> targets, int tabEntry)
        {
            private string? _section;
            private int _last = tabEntry; // the entry a following hint belongs to; a hint at the top describes the tab

            public void Visit(DependencyObject node)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
                {
                    switch (child)
                    {
                        case TextBlock text:
                            VisitText(text);
                            break;

                        // Check boxes, radio buttons and buttons are found by their text; what's inside isn't searched.
                        case ButtonBase button:
                            if (TextOf(button.Content) is { Length: > 0 } label)
                                Add(label, button);
                            break;

                        // Lists and drop-downs hold data (folders, apps, hotkey rows), not settings.
                        case ItemsControl:
                            break;

                        default:
                            Visit(child);
                            break;
                    }
                }
            }

            private void VisitText(TextBlock text)
            {
                var content = Clean(text.Text);
                if (content.Length == 0)
                    return;

                if (IsStyled(text, "Section"))
                {
                    _section = content;
                    Add(content, text);
                }
                else if (IsStyled(text, "Label"))
                {
                    Add(content, text, isLabel: true);
                }
                // Named hints are filled in by code (status lines, name previews, the version), so they don't
                // describe a setting.
                else if (IsStyled(text, "Hint") && string.IsNullOrEmpty(text.Name) && IsShown(text))
                {
                    var entry = entries[_last];
                    entries[_last] = entry with { Hint = entry.Hint is null ? content : $"{entry.Hint} {content}" };
                }
            }

            private void Add(string label, FrameworkElement element, bool isLabel = false)
            {
                entries.Add(new SettingsSearchEntry(label, tabName, _section));
                targets.Add(new Target(tab, element, isLabel));
                _last = entries.Count - 1;
            }

            private static bool IsStyled(FrameworkElement element, string key) =>
                element.Style is { } style && ReferenceEquals(style, element.TryFindResource(key));
        }

        private static string? TextOf(object? content) => content switch
        {
            string text => Clean(text),
            TextBlock block => Clean(block.Text),
            _ => null,
        };

        private static string Clean(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The highlight drawn round a setting the search jumped to; it fades out by itself.</summary>
    internal sealed class SearchHighlightAdorner : Adorner
    {
        private readonly Brush _fill;
        private readonly Pen _pen;

        public SearchHighlightAdorner(UIElement adorned, Color accent) : base(adorned)
        {
            IsHitTestVisible = false;
            _fill = new SolidColorBrush(accent) { Opacity = 0.18 };
            _fill.Freeze();
            var stroke = new SolidColorBrush(accent);
            stroke.Freeze();
            _pen = new Pen(stroke, 2);
            _pen.Freeze();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var bounds = new Rect(AdornedElement.RenderSize);
            bounds.Inflate(4, 2);
            drawingContext.DrawRoundedRectangle(_fill, _pen, bounds, 4, 4);
        }
    }
}
