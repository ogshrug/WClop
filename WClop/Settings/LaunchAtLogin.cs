using Microsoft.Win32;

namespace WClop.Settings
{
    /// <summary>"Start WClop when I sign in", via the per-user Run key (project.md §26.1).</summary>
    internal static class LaunchAtLogin
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "WClop";

        private static string Command => $"\"{Environment.ProcessPath}\"";

        public static bool IsEnabled
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value
                       && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
                key.SetValue(ValueName, Command);
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
