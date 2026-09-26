using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static WClop.Clipboard.NativeMethods;

namespace WClop.Clipboard
{
    /// <summary>
    /// Raw Win32 clipboard access. Every method except <see cref="TryOpen"/> and <see cref="OwnerProcess"/>
    /// must be called between <see cref="TryOpen"/> and <see cref="Close"/>, on the clipboard thread.
    /// </summary>
    internal static class ClipboardAccess
    {
        private static readonly Dictionary<uint, string> StandardFormatNames = new()
        {
            [1] = "CF_TEXT", [2] = "CF_BITMAP", [3] = "CF_METAFILEPICT", [4] = "CF_SYLK", [5] = "CF_DIF",
            [6] = "CF_TIFF", [7] = "CF_OEMTEXT", [8] = "CF_DIB", [9] = "CF_PALETTE", [10] = "CF_PENDATA",
            [11] = "CF_RIFF", [12] = "CF_WAVE", [13] = "CF_UNICODETEXT", [14] = "CF_ENHMETAFILE",
            [15] = "CF_HDROP", [16] = "CF_LOCALE", [17] = "CF_DIBV5",
        };

        /// <summary>
        /// Another app may hold the clipboard open; retry briefly (project.md §26.1: 10 × 20 ms).
        /// </summary>
        public static bool TryOpen(IntPtr owner)
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (OpenClipboard(owner))
                    return true;
                Thread.Sleep(20);
            }

            return false;
        }

        public static void Close() => CloseClipboard();

        public static List<(uint Id, string Name)> EnumerateFormats()
        {
            var formats = new List<(uint, string)>();
            var buffer = new char[256];
            for (var format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format))
            {
                if (StandardFormatNames.TryGetValue(format, out var name))
                {
                    formats.Add((format, name));
                    continue;
                }

                var length = GetClipboardFormatName(format, buffer, buffer.Length);
                formats.Add((format, length > 0 ? new string(buffer, 0, length) : $"#{format}"));
            }

            return formats;
        }

        /// <summary>The raw bytes of an HGLOBAL-based format, or null if it can't be rendered.</summary>
        public static byte[]? ReadBytes(uint format)
        {
            var handle = GetClipboardData(format);
            if (handle == IntPtr.Zero)
                return null;

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
                return null;
            try
            {
                var size = checked((int)GlobalSize(handle).ToUInt64());
                var bytes = new byte[size];
                Marshal.Copy(pointer, bytes, 0, size);
                return bytes;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }

        public static uint? ReadDword(uint format) =>
            ReadBytes(format) is { Length: >= 4 } bytes ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : null;

        /// <summary>Parses the DROPFILES structure behind CF_HDROP.</summary>
        public static List<string> ReadFiles()
        {
            var files = new List<string>();
            if (ReadBytes(CF_HDROP) is not { Length: >= 20 } data)
                return files;

            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data);
            var wide = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)) != 0;
            var encoding = wide ? Encoding.Unicode : Encoding.Default;
            var charSize = wide ? 2 : 1;

            var start = offset;
            for (var i = offset; i + charSize <= data.Length; i += charSize)
            {
                var isNull = wide ? data[i] == 0 && data[i + 1] == 0 : data[i] == 0;
                if (!isNull)
                    continue;
                if (i == start)
                    break; // double terminator
                files.Add(encoding.GetString(data, start, i - start));
                start = i + charSize;
            }

            return files;
        }

        /// <summary>Allocates movable global memory for <paramref name="data"/> and hands it to the clipboard.</summary>
        public static bool Write(uint format, byte[] data)
        {
            var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)Math.Max(data.Length, 1));
            if (handle == IntPtr.Zero)
                return false;

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                GlobalFree(handle);
                return false;
            }

            Marshal.Copy(data, 0, pointer, data.Length);
            GlobalUnlock(handle);

            // On success the system owns the memory; on failure we still do.
            if (SetClipboardData(format, handle) != IntPtr.Zero)
                return true;
            GlobalFree(handle);
            return false;
        }

        public static byte[] DropFiles(string path)
        {
            const int headerSize = 20;
            var pathBytes = Encoding.Unicode.GetBytes(path + "\0\0");
            var data = new byte[headerSize + pathBytes.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(data, headerSize); // pFiles
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 1); // fWide
            pathBytes.CopyTo(data, headerSize);
            return data;
        }

        public static byte[] Dword(uint value)
        {
            var data = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(data, value);
            return data;
        }

        /// <summary>
        /// Executable name of the clipboard owner, falling back to the foreground window when the owner is
        /// missing (delayed rendering by an exited app, or no owner window).
        /// </summary>
        public static string? OwnerProcess() =>
            ProcessNameOf(GetClipboardOwner()) ?? ProcessNameOf(GetForegroundWindow());

        private static string? ProcessNameOf(IntPtr window)
        {
            if (window == IntPtr.Zero)
                return null;
            GetWindowThreadProcessId(window, out var processId);
            if (processId == 0)
                return null;
            try
            {
                using var process = Process.GetProcessById((int)processId);
                return process.ProcessName + ".exe";
            }
            catch (ArgumentException)
            {
                return null; // exited
            }
        }
    }
}
