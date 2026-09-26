using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using WClop.Core.Hotkeys;
using WClop.Core.Settings;

namespace WClop.Settings
{
    /// <summary>Shows a byte count in KB or MB (<see cref="Unit"/> bytes each) and parses it back.</summary>
    public sealed class BytesConverter : IValueConverter
    {
        public double Unit { get; set; } = 1;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is long bytes ? (bytes / Unit).ToString("0.##", culture) : "";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            double.TryParse(value as string, NumberStyles.Float, culture, out var number) && number >= 0
                ? (long)Math.Round(number * Unit)
                : DependencyProperty.UnsetValue;
    }

    /// <summary>A list of strings as "a, b, c".</summary>
    public sealed class CommaListConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is IEnumerable<string> items ? string.Join(", ", items) : "";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            ((value as string) ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.TrimStart('.'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>An enum value with a friendly label, for combo boxes.</summary>
    internal sealed record Choice<T>(T Value, string Label);

    /// <summary>One row of the Hotkeys tab: an action, its key, whether it's on, and whether it works.</summary>
    internal sealed class HotkeyRow : INotifyPropertyChanged
    {
        private readonly HotkeySettings _settings;
        private readonly HotkeyCommand _command;
        private string _status = "";

        public HotkeyRow(HotkeyCommand command, HotkeySettings settings)
        {
            _command = command;
            _settings = settings;
            KeyChoices = command.Action == HotkeyAction.ScaleTo
                ? [HotkeyCatalog.DigitsKey]
                : HotkeyCatalog.AssignableKeys;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Raised when the user changes the key or switches the action on or off.</summary>
        public event Action? Edited;

        public HotkeyAction Action => _command.Action;
        public string Description => _command.Description;
        public IReadOnlyList<string> KeyChoices { get; }
        public bool CanRemap => _command.Action != HotkeyAction.ScaleTo;

        public string Key
        {
            get => HotkeyCatalog.KeyFor(_command, _settings);
            set
            {
                if (!CanRemap || value == Key)
                    return;
                if (value == _command.DefaultKey)
                    _settings.Keys.Remove(Action.ToString());
                else
                    _settings.Keys[Action.ToString()] = value;
                Notify();
                Edited?.Invoke();
            }
        }

        public bool IsOn
        {
            get => HotkeyCatalog.IsEnabled(Action, _settings);
            set
            {
                if (value == IsOn)
                    return;
                if (value)
                    _settings.DisabledActions.RemoveAll(a => string.Equals(a, Action.ToString(), StringComparison.OrdinalIgnoreCase));
                else
                    _settings.DisabledActions.Add(Action.ToString());
                Notify();
                Edited?.Invoke();
            }
        }

        public string Status
        {
            get => _status;
            set
            {
                if (_status == value)
                    return;
                _status = value;
                Notify();
                Notify(nameof(HasProblem));
            }
        }

        public bool HasProblem => Status is not ("Active" or "Off" or "Hotkeys are off");

        /// <summary>After a reset, re-read everything from the settings.</summary>
        public void Reload()
        {
            Notify(nameof(Key));
            Notify(nameof(IsOn));
        }

        private void Notify([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
