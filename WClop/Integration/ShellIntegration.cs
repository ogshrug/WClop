using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WClop.Core.DropZone;
using WClop.Core.Logging;
using WClop.Core.Media;

namespace WClop.Integration
{
    /// <summary>
    /// "Optimise with WClop" in Explorer's right-click menu, and WClop in the Send To menu. Both are per-user
    /// (HKCU and the user's SendTo folder), so no admin rights are needed.
    /// On Windows 11 the verb shows under "Show more options"; the modern menu needs package identity (Phase 12).
    /// </summary>
    internal static class ShellIntegration
    {
        private const string VerbName = "WClop.Optimise";
        private const string DirectoryVerbKey = @"Software\Classes\Directory\shell\" + VerbName;

        private static string Exe => Environment.ProcessPath!;

        private static IEnumerable<string> Extensions =>
            FileFormats.KnownExtensions.Where(e => DropInputs.OptimisableMedia.Contains(e.Format)).Select(e => "." + e.Extension);

        private static string FileVerbKey(string extension) => $@"Software\Classes\SystemFileAssociations\{extension}\shell\{VerbName}";

        private static string SendToShortcut =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "WClop (optimise).lnk");

        // Explorer menu

        public static bool IsContextMenuEnabled
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(DirectoryVerbKey + @"\command");
                return key?.GetValue(null) is string command && command.Contains(Exe, StringComparison.OrdinalIgnoreCase);
            }
        }

        public static void SetContextMenuEnabled(bool enabled)
        {
            if (enabled)
            {
                // Explorer starts one process per selected item; the running app gathers them into one request.
                var command = $"\"{Exe}\" --explorer \"%1\"";
                foreach (var extension in Extensions)
                    WriteVerb(FileVerbKey(extension), "Optimise with WClop", command);
                WriteVerb(DirectoryVerbKey, "Optimise folder with WClop", command);
            }
            else
            {
                foreach (var extension in Extensions)
                    Registry.CurrentUser.DeleteSubKeyTree(FileVerbKey(extension), throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree(DirectoryVerbKey, throwOnMissingSubKey: false);
            }

            SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
            Log.Info($"Explorer menu {(enabled ? "added" : "removed")}");
        }

        private static void WriteVerb(string path, string label, string command)
        {
            using var verb = Registry.CurrentUser.CreateSubKey(path);
            verb.SetValue("MUIVerb", label);
            verb.SetValue("Icon", $"\"{Exe}\",0");
            // Without this Explorer hides the verb when more than 15 items are selected.
            verb.SetValue("MultiSelectModel", "Player");
            using var commandKey = verb.CreateSubKey("command");
            commandKey.SetValue(null, command);
        }

        // Send To

        public static bool IsSendToEnabled => File.Exists(SendToShortcut);

        public static void SetSendToEnabled(bool enabled)
        {
            if (!enabled)
            {
                File.Delete(SendToShortcut);
                return;
            }

            // Send To passes every selected item to one process.
            var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("WScript.Shell isn't available");
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                var shortcut = shell.CreateShortcut(SendToShortcut);
                shortcut.TargetPath = Exe;
                shortcut.Arguments = "--send-to";
                shortcut.IconLocation = Exe + ",0";
                shortcut.Description = "Optimise with WClop";
                shortcut.Save();
                Marshal.FinalReleaseComObject(shortcut);
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }

        /// <summary>
        /// Keeps existing registrations pointing at this exe after WClop moves (or a new build runs from elsewhere).
        /// </summary>
        public static void Refresh()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(DirectoryVerbKey + @"\command");
                if (key is not null && !IsContextMenuEnabled)
                    SetContextMenuEnabled(true);
                if (IsSendToEnabled)
                    SetSendToEnabled(true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException)
            {
                Log.Warn($"Couldn't refresh the Explorer integration: {e.Message}");
            }
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    }
}
