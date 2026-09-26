using System.IO;
using WClop.Core.Ipc;
using WClop.Core.Logging;

namespace WClop.Integration
{
    /// <summary>
    /// What WClop.exe was asked to do on its command line:
    /// <c>--explorer</c> / <c>--send-to</c> &lt;files&gt; (the right-click menu and Send To), <c>--batch</c> &lt;paths&gt;,
    /// and <c>--settings</c>. A second launch forwards these to the running instance over the local API.
    /// </summary>
    internal sealed record LaunchArgs(IReadOnlyList<string> Files, IpcSource Source, IReadOnlyList<string> Batch, bool Settings)
    {
        /// <summary><c>--quit</c>: ask the running WClop to exit (the installer does this before replacing files).</summary>
        public bool Quit { get; init; }

        /// <summary><c>--unregister</c>: remove the Explorer menu, Send To and start-at-sign-in entries (uninstall).</summary>
        public bool Unregister { get; init; }

        /// <summary><c>--register</c>: add the Explorer menu and Send To entries (the installer's option).</summary>
        public bool Register { get; init; }

        public bool IsEmpty => Files.Count == 0 && Batch.Count == 0 && !Settings;

        public static LaunchArgs Parse(string[] args)
        {
            var explorer = After(args, "--explorer");
            var sendTo = After(args, "--send-to");
            return new LaunchArgs(
                [.. explorer, .. sendTo],
                sendTo.Count > 0 ? IpcSource.SendTo : IpcSource.Explorer,
                After(args, "--batch"),
                args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
            {
                Quit = args.Contains("--quit", StringComparer.OrdinalIgnoreCase),
                Unregister = args.Contains("--unregister", StringComparer.OrdinalIgnoreCase),
                Register = args.Contains("--register", StringComparer.OrdinalIgnoreCase),
            };
        }

        /// <summary>The paths following <paramref name="flag"/>, up to the next flag.</summary>
        private static List<string> After(string[] args, string flag)
        {
            var index = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
            return index < 0
                ? []
                : args.Skip(index + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        }

        /// <summary>
        /// Hands the request to the instance that's already running. It may be starting up itself (Explorer launches
        /// one process per selected file at once), so keep trying for a few seconds.
        /// </summary>
        /// <summary>Asks a running WClop to exit and waits (up to a few seconds) until it has.</summary>
        public static bool QuitRunning()
        {
            try
            {
                IpcClient.SendAsync<SettingsCommand, SettingsReply>(IpcChannel.Settings, new SettingsCommand("quit"), TimeSpan.FromSeconds(2))
                    .GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            for (var i = 0; i < 50; i++)
            {
                if (!Mutex.TryOpenExisting(@"Local\WClop.SingleInstance", out var existing))
                    return true;
                existing.Dispose();
                Thread.Sleep(100);
            }

            return false;
        }

        public async Task<bool> ForwardAsync()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (true)
            {
                try
                {
                    if (await SendAsync())
                        return true;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Couldn't reach the running WClop: {e.Message}");
                }

                if (DateTime.UtcNow > deadline)
                    return false;
                await Task.Delay(250);
            }
        }

        private async Task<bool> SendAsync()
        {
            var timeout = TimeSpan.FromMilliseconds(500);
            if (Files.Count > 0
                && await IpcClient.SendAsync<OptimiseCommand, OptimiseReply>(
                    IpcChannel.Optimise, new OptimiseCommand { Paths = Files, Source = Source }, timeout) is null)
                return false;
            if (Batch.Count > 0
                && await IpcClient.SendAsync<OptimiseCommand, OptimiseReply>(
                    IpcChannel.Optimise, new OptimiseCommand { Paths = Batch, Batch = true }, timeout) is null)
                return false;
            if (Settings
                && await IpcClient.SendAsync<SettingsCommand, SettingsReply>(IpcChannel.Settings, new SettingsCommand("show"), timeout) is null)
                return false;
            return true;
        }
    }
}
