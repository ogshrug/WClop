using System.Runtime.InteropServices;

namespace WClop.DropZone
{
    /// <summary>
    /// Tells a drag-and-drop apart from other mouse drags by the cursor. During an OLE drag the cursor is one of the
    /// drag-and-drop cursors (copy / move / link / no-drop), which are not the standard system cursors; selecting text
    /// or moving and resizing windows keep the arrow, I-beam or sizing cursors.
    /// </summary>
    internal static class DragCursor
    {
        private static readonly int[] StandardCursorIds =
        [
            32512, // arrow
            32513, // I-beam
            32514, // wait
            32515, // cross
            32516, // up arrow
            32642, 32643, 32644, 32645, 32646, // sizing
            32649, // hand
            32650, // app starting
            32651, // help
            32671, // pin
            32672, // person
        ];

        private static readonly Lazy<HashSet<IntPtr>> StandardCursors = new(() =>
            StandardCursorIds.Select(id => LoadCursor(IntPtr.Zero, (IntPtr)id)).Where(h => h != IntPtr.Zero).ToHashSet());

        /// <summary>True if the visible cursor isn't a standard system cursor, i.e. probably a drag-and-drop cursor.</summary>
        public static bool LooksLikeDragAndDrop()
        {
            var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
            if (!GetCursorInfo(ref info) || (info.Flags & 1) == 0 || info.Handle == IntPtr.Zero)
                return false;
            return !StandardCursors.Value.Contains(info.Handle);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CursorInfo
        {
            public int Size;
            public int Flags;
            public IntPtr Handle;
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorInfo(ref CursorInfo info);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);
    }
}
