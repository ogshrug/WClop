using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace WClop.Results
{
    /// <summary>
    /// The Windows share sheet for result files (project.md §20.6): DataTransferManager for the results window through
    /// IDataTransferManagerInterop, filled with StorageFiles when Windows asks for the data. Plain COM interop against
    /// the WinRT ABI, so the app needs no Windows SDK projection.
    /// </summary>
    internal static class ShareSheet
    {
        private static readonly Guid IID_IDataTransferManagerInterop = new("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8");
        private static readonly Guid IID_IDataTransferManager = new("a5caee9b-8708-49d1-8d36-67d25a8da00c");
        private static readonly Guid IID_IStorageFileStatics = new("5984c710-daf2-43c8-8bb4-a4d3eacfd03f");
        internal static readonly Guid IID_IStorageItem = new("4207a996-ca2f-42f7-bde8-8b10457a7f30");
        internal static readonly Guid IID_IInspectable = new("AF86E2E0-B12D-4c6a-9C5A-D7AA65101E90");
        internal static readonly Guid IID_IAgileObject = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");

        private static readonly TimeSpan FileLookupTimeout = TimeSpan.FromSeconds(10);

        /// <summary>The handler registered per window; replaced on each share so only the latest files are offered.</summary>
        private static readonly Dictionary<IntPtr, Registration> Registrations = [];

        private sealed record Registration(IDataTransferManager Manager, long Token, DataRequestedHandler Handler);

        /// <summary>Opens the share sheet for <paramref name="paths"/>, anchored to <paramref name="hwnd"/>. Throws if Windows refuses.</summary>
        public static async Task ShowAsync(IntPtr hwnd, IReadOnlyList<string> paths)
        {
            // Looking files up is asynchronous in WinRT; do it before asking, so the data request is answered at once.
            var files = await Task.Run(() => paths.Select(GetStorageFile).ToList());
            try
            {
                var interop = (IDataTransferManagerInterop)ActivationFactory(
                    "Windows.ApplicationModel.DataTransfer.DataTransferManager", IID_IDataTransferManagerInterop);
                var iid = IID_IDataTransferManager;
                Check(interop.GetForWindow(hwnd, ref iid, out var managerPointer));
                var manager = (IDataTransferManager)Take(managerPointer);

                if (Registrations.Remove(hwnd, out var previous))
                {
                    previous.Manager.remove_DataRequested(previous.Token);
                    previous.Handler.Dispose();
                }

                var title = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} files";
                var handler = new DataRequestedHandler(title, files);
                files = []; // the handler owns them now
                var handlerPointer = Marshal.GetComInterfaceForObject(handler, typeof(ITypedEventHandlerDataRequested));
                try
                {
                    Check(manager.add_DataRequested(handlerPointer, out var token));
                    Registrations[hwnd] = new Registration(manager, token, handler);
                }
                finally
                {
                    Marshal.Release(handlerPointer);
                }

                Check(interop.ShowShareUIForWindow(hwnd));
            }
            finally
            {
                foreach (var file in files)
                    Marshal.Release(file);
            }
        }

        /// <summary>A StorageFile (IStorageFile*) for a path; waits for the asynchronous lookup.</summary>
        private static IntPtr GetStorageFile(string path)
        {
            var statics = (IStorageFileStatics)ActivationFactory("Windows.Storage.StorageFile", IID_IStorageFileStatics);
            using var hstring = new HString(path);
            Check(statics.GetFileFromPathAsync(hstring.Handle, out var operationPointer));
            var operation = (IAsyncOperationStorageFile)Take(operationPointer);
            var info = (IAsyncInfo)operation;

            var deadline = DateTime.UtcNow + FileLookupTimeout;
            int status;
            while (true)
            {
                Check(info.get_Status(out status));
                if (status != 0) // Started
                    break;
                if (DateTime.UtcNow > deadline)
                {
                    info.Cancel();
                    throw new TimeoutException($"Windows took too long to find {Path.GetFileName(path)}");
                }

                Thread.Sleep(10);
            }

            if (status != 1) // Completed
            {
                info.get_ErrorCode(out var error);
                Marshal.ThrowExceptionForHR(error != 0 ? error : unchecked((int)0x80004004)); // E_ABORT when cancelled
            }

            Check(operation.GetResults(out var file));
            return file;
        }

        private static object ActivationFactory(string className, Guid iid)
        {
            using var name = new HString(className);
            Check(RoGetActivationFactory(name.Handle, ref iid, out var factory));
            return Take(factory);
        }

        /// <summary>An RCW for an interface pointer we were given (and now hand over).</summary>
        private static object Take(IntPtr pointer)
        {
            try
            {
                return Marshal.GetObjectForIUnknown(pointer);
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }

        internal static void Check(int hresult)
        {
            if (hresult < 0)
                Marshal.ThrowExceptionForHR(hresult);
        }

        internal sealed class HString : IDisposable
        {
            public HString(string text) => Check(WindowsCreateString(text, text.Length, out _handle));

            private readonly IntPtr _handle;

            public IntPtr Handle => _handle;

            public void Dispose() => WindowsDeleteString(_handle);
        }

        [DllImport("combase.dll", PreserveSig = true)]
        private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

        [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

        [DllImport("combase.dll", PreserveSig = true)]
        private static extern int WindowsDeleteString(IntPtr hstring);
    }

    /// <summary>Answers DataTransferManager.DataRequested: a title, and the files as storage items.</summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class DataRequestedHandler : ITypedEventHandlerDataRequested, ICustomQueryInterface, IDisposable
    {
        private readonly string _title;
        private readonly List<IntPtr> _files;

        internal DataRequestedHandler(string title, List<IntPtr> files)
        {
            _title = title;
            _files = files;
        }

        public int Invoke(IntPtr sender, IntPtr args)
        {
            try
            {
                var eventArgs = (IDataRequestedEventArgs)Marshal.GetObjectForIUnknown(args);
                ShareSheet.Check(eventArgs.get_Request(out var requestPointer));
                var request = (IDataRequest)Borrow(requestPointer);
                ShareSheet.Check(request.get_Data(out var packagePointer));
                var package = (IDataPackage)Borrow(packagePointer);
                ShareSheet.Check(package.get_Properties(out var propertiesPointer));
                var properties = (IDataPackagePropertySet)Borrow(propertiesPointer);

                using (var title = new ShareSheet.HString(_title))
                    ShareSheet.Check(properties.put_Title(title.Handle));
                using (var description = new ShareSheet.HString("Shared from WClop"))
                    ShareSheet.Check(properties.put_Description(description.Handle));

                var items = new StorageItems(_files);
                var itemsPointer = Marshal.GetComInterfaceForObject(items, typeof(IIterableStorageItem));
                try
                {
                    ShareSheet.Check(package.SetStorageItems(itemsPointer, 1));
                }
                finally
                {
                    Marshal.Release(itemsPointer);
                }

                return 0;
            }
            catch (Exception e)
            {
                WClop.Core.Logging.Log.Error("Filling in the share sheet failed", e);
                return Marshal.GetHRForException(e);
            }
        }

        public CustomQueryInterfaceResult GetInterface(ref Guid iid, out IntPtr ppv) =>
            WinRtObjects.AnswerInspectable(this, typeof(ITypedEventHandlerDataRequested), iid, out ppv);

        public void Dispose()
        {
            foreach (var file in _files)
                Marshal.Release(file);
            _files.Clear();
        }

        private static object Borrow(IntPtr pointer)
        {
            try
            {
                return Marshal.GetObjectForIUnknown(pointer);
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }
    }

    /// <summary>IIterable&lt;IStorageItem&gt; over StorageFiles, for DataPackage.SetStorageItems.</summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class StorageItems : IIterableStorageItem, ICustomQueryInterface
    {
        private readonly IReadOnlyList<IntPtr> _files;

        internal StorageItems(IReadOnlyList<IntPtr> files) => _files = files;

        public int GetIids(out int iidCount, out IntPtr iids) => WinRtObjects.NoIids(out iidCount, out iids);
        public int GetRuntimeClassName(out IntPtr className) => WinRtObjects.NoClassName(out className);
        public int GetTrustLevel(out int trustLevel) => WinRtObjects.BaseTrust(out trustLevel);

        public int First(out IntPtr iterator)
        {
            iterator = Marshal.GetComInterfaceForObject(new StorageItemIterator(_files), typeof(IIteratorStorageItem));
            return 0;
        }

        public CustomQueryInterfaceResult GetInterface(ref Guid iid, out IntPtr ppv) =>
            WinRtObjects.AnswerInspectable(this, typeof(IIterableStorageItem), iid, out ppv);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class StorageItemIterator : IIteratorStorageItem, ICustomQueryInterface
    {
        private const int E_BOUNDS = unchecked((int)0x8000000B);

        private readonly IReadOnlyList<IntPtr> _files;
        private int _index;

        internal StorageItemIterator(IReadOnlyList<IntPtr> files) => _files = files;

        public int GetIids(out int iidCount, out IntPtr iids) => WinRtObjects.NoIids(out iidCount, out iids);
        public int GetRuntimeClassName(out IntPtr className) => WinRtObjects.NoClassName(out className);
        public int GetTrustLevel(out int trustLevel) => WinRtObjects.BaseTrust(out trustLevel);

        public int get_Current(out IntPtr current)
        {
            current = IntPtr.Zero;
            return _index < _files.Count ? StorageItem(_files[_index], out current) : E_BOUNDS;
        }

        public int get_HasCurrent(out byte hasCurrent)
        {
            hasCurrent = _index < _files.Count ? (byte)1 : (byte)0;
            return 0;
        }

        public int MoveNext(out byte hasCurrent)
        {
            if (_index < _files.Count)
                _index++;
            return get_HasCurrent(out hasCurrent);
        }

        public int GetMany(uint capacity, IntPtr items, out uint actual)
        {
            actual = 0;
            while (actual < capacity && _index < _files.Count)
            {
                var hr = StorageItem(_files[_index], out var item);
                if (hr < 0)
                    return hr;
                Marshal.WriteIntPtr(items, (int)actual * IntPtr.Size, item);
                actual++;
                _index++;
            }

            return 0;
        }

        public CustomQueryInterfaceResult GetInterface(ref Guid iid, out IntPtr ppv) =>
            WinRtObjects.AnswerInspectable(this, typeof(IIteratorStorageItem), iid, out ppv);

        private static int StorageItem(IntPtr file, out IntPtr item)
        {
            var iid = ShareSheet.IID_IStorageItem;
            return Marshal.QueryInterface(file, in iid, out item);
        }
    }

    /// <summary>The IInspectable basics for objects WClop hands to WinRT.</summary>
    internal static class WinRtObjects
    {
        public static int NoIids(out int count, out IntPtr iids)
        {
            count = 0;
            iids = IntPtr.Zero;
            return 0;
        }

        public static int NoClassName(out IntPtr className)
        {
            className = IntPtr.Zero; // an empty HSTRING
            return 0;
        }

        public static int BaseTrust(out int trustLevel)
        {
            trustLevel = 0;
            return 0;
        }

        /// <summary>
        /// .NET's COM wrappers don't know IInspectable or IAgileObject; every interface here starts with IInspectable's
        /// methods and the objects are free-threaded, so the main interface answers for both.
        /// </summary>
        public static CustomQueryInterfaceResult AnswerInspectable(object target, Type main, Guid iid, out IntPtr ppv)
        {
            if (iid == ShareSheet.IID_IInspectable || iid == ShareSheet.IID_IAgileObject)
            {
                ppv = Marshal.GetComInterfaceForObject(target, main, CustomQueryInterfaceMode.Ignore);
                return CustomQueryInterfaceResult.Handled;
            }

            ppv = IntPtr.Zero;
            return CustomQueryInterfaceResult.NotHandled;
        }
    }

    // The WinRT ABI interfaces used above (Windows SDK 10.0.19041 headers). Methods before the ones WClop calls only hold
    // their vtable slots; IInspectable's three come first in every WinRT interface.

    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDataTransferManagerInterop
    {
        [PreserveSig] int GetForWindow(IntPtr appWindow, ref Guid riid, out IntPtr dataTransferManager);
        [PreserveSig] int ShowShareUIForWindow(IntPtr appWindow);
    }

    [ComImport]
    [Guid("a5caee9b-8708-49d1-8d36-67d25a8da00c")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDataTransferManager
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
        [PreserveSig] int add_DataRequested(IntPtr handler, out long token);
        [PreserveSig] int remove_DataRequested(long token);
    }

    [ComImport]
    [Guid("cb8ba807-6ac5-43c9-8ac5-9ba232163182")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDataRequestedEventArgs
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
        [PreserveSig] int get_Request(out IntPtr request);
    }

    [ComImport]
    [Guid("4341ae3b-fc12-4e53-8c02-ac714c415a27")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDataRequest
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
        [PreserveSig] int get_Data(out IntPtr package);
    }

    [ComImport]
    [Guid("61ebf5c7-efea-4346-9554-981d7e198ffe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDataPackage
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
        void GetView();
        [PreserveSig] int get_Properties(out IntPtr properties);
        void get_RequestedOperation();
        void put_RequestedOperation();
        void add_OperationCompleted();
        void remove_OperationCompleted();
        void add_Destroyed();
        void remove_Destroyed();
        void SetData();
        void SetDataProvider();
        void SetText();
        void SetUri();
        void SetHtmlFormat();
        void get_ResourceMap();
        void SetRtf();
        void SetBitmap();
        void SetStorageItemsReadOnly();
        [PreserveSig] int SetStorageItems(IntPtr items, byte readOnly);
    }

    [ComImport]
    [Guid("cd1c93eb-4c4c-443a-a8d3-f5c241e91689")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDataPackagePropertySet
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
        void get_Title();
        [PreserveSig] int put_Title(IntPtr title);
        void get_Description();
        [PreserveSig] int put_Description(IntPtr description);
    }

    [ComImport]
    [Guid("5984c710-daf2-43c8-8bb4-a4d3eacfd03f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IStorageFileStatics
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
        [PreserveSig] int GetFileFromPathAsync(IntPtr path, out IntPtr operation);
    }

    /// <summary>IAsyncOperation&lt;StorageFile&gt;.</summary>
    [ComImport]
    [Guid("5e52f8ce-aced-5a42-95b4-f674dd84885e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAsyncOperationStorageFile
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
        void put_Completed();
        void get_Completed();
        [PreserveSig] int GetResults(out IntPtr file);
    }

    [ComImport]
    [Guid("00000036-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAsyncInfo
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
        [PreserveSig] int get_Id(out uint id);
        [PreserveSig] int get_Status(out int status);
        [PreserveSig] int get_ErrorCode(out int errorCode);
        [PreserveSig] int Cancel();
    }

    /// <summary>TypedEventHandler&lt;DataTransferManager, DataRequestedEventArgs&gt; (a delegate: IUnknown, then Invoke).</summary>
    [ComVisible(true)]
    [Guid("ec6f9cc8-46d0-5e0e-b4d2-7d7773ae37a0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITypedEventHandlerDataRequested
    {
        [PreserveSig] int Invoke(IntPtr sender, IntPtr args);
    }

    /// <summary>IIterable&lt;IStorageItem&gt;.</summary>
    [ComVisible(true)]
    [Guid("bb8b8418-65d1-544b-b083-6d172f568c73")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IIterableStorageItem
    {
        [PreserveSig] int GetIids(out int iidCount, out IntPtr iids);
        [PreserveSig] int GetRuntimeClassName(out IntPtr className);
        [PreserveSig] int GetTrustLevel(out int trustLevel);
        [PreserveSig] int First(out IntPtr iterator);
    }

    /// <summary>IIterator&lt;IStorageItem&gt;.</summary>
    [ComVisible(true)]
    [Guid("05b487c2-3830-5d3c-98da-25fa11542dbd")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IIteratorStorageItem
    {
        [PreserveSig] int GetIids(out int iidCount, out IntPtr iids);
        [PreserveSig] int GetRuntimeClassName(out IntPtr className);
        [PreserveSig] int GetTrustLevel(out int trustLevel);
        [PreserveSig] int get_Current(out IntPtr current);
        [PreserveSig] int get_HasCurrent(out byte hasCurrent);
        [PreserveSig] int MoveNext(out byte hasCurrent);
        [PreserveSig] int GetMany(uint capacity, IntPtr items, out uint actual);
    }
}
