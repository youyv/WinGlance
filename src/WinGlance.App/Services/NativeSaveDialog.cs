using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinGlance.App.Services;

// IFileSaveDialog only selects a filesystem path. Unlike the SDK 1.8 picker,
// this does not create the destination before our writer has a complete snapshot.
internal static class NativeSaveDialog
{
    private const int Cancelled = unchecked((int)0x800704C7);
    private const uint CancelMessage = 0x8000 + 71, TimerMessage = 0x113;

    internal static Task<string?> PickAsync(nint owner, string suggestedName, string extension, CancellationToken token, Action<string>? diagnostic = null)
    {
        if (extension is not ("csv" or "json")) throw new ArgumentException("Unsupported export format.", nameof(extension));
        if (token.IsCancellationRequested) return Task.FromCanceled<string?>(token);
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => Run(owner, suggestedName, extension, token, completion, diagnostic))
        { IsBackground = true, Name = "WinGlance save location" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Run(nint owner, string name, string extension, CancellationToken token,
        TaskCompletionSource<string?> completion, Action<string>? diagnostic)
    {
        IFileDialog? dialog = null;
        IShellItem? result = null;
        nint window = 0;
        nuint timer = 0;
        bool initialized = false, subclassed = false, showing = false, finishing = false, closing = false;
        int cancelAttempts = 0;
        CancellationTokenRegistration registration = default;
        string? selected = null;
        Exception? failure = null;
        void Trace(string message)
        {
            try { diagnostic?.Invoke(message); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
        SubclassProcedure callback = (hwnd, message, wParam, lParam, id, data) =>
        {
            if (diagnostic is not null && (message == CancelMessage || message == TimerMessage && token.IsCancellationRequested)) Trace($"message:{message} showing:{showing} finishing:{finishing}");
            // A queued timer can survive KillTimer. Never call COM during teardown.
            if (!finishing && showing && hwnd == window && token.IsCancellationRequested &&
                (message == CancelMessage || message == TimerMessage && wParam == timer) && !closing)
            {
                closing = true;
                try
                {
                    int? resultCode = dialog?.Close(Cancelled);
                    if (diagnostic is not null) Trace($"close:{resultCode:X8}");
                    // Some Shell versions acknowledge Close while keeping Show open.
                    // Request the normal close action on this exact dialog, on its STA.
                    // Never send close to the app owner or search other windows.
                    if (++cancelAttempts > 1 && dialog is IOleWindow native)
                    {
                        native.GetWindow(out nint dialogWindow);
                        uint thread = GetWindowThreadProcessId(dialogWindow, out uint process);
                        if (dialogWindow != 0 && thread == GetCurrentThreadId() && process == GetCurrentProcessId())
                        {
                            bool posted = PostMessage(dialogWindow, 0x10, 0, 0);
                            if (diagnostic is not null) Trace($"dialog-close-post:{posted}");
                        }
                    }
                }
                catch (Exception error) when (error is not OutOfMemoryException) { /* Retry on the next tick. */ }
                finally { closing = false; }
            }
            return DefSubclassProc(hwnd, message, wParam, lParam);
        };
        try
        {
            token.ThrowIfCancellationRequested();
            Marshal.ThrowExceptionForHR(CoInitializeEx(0, 2));
            initialized = true; // S_FALSE also needs its matching CoUninitialize.
            var type = Type.GetTypeFromCLSID(new Guid("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B"), true)!;
            dialog = (IFileDialog)Activator.CreateInstance(type)!;
            dialog.GetOptions(out uint options);
            // OVERWRITEPROMPT | STRICTFILETYPES | FORCEFILESYSTEM | PATHMUSTEXIST |
            // NOTESTFILECREATE. A new destination is allowed; no test file is written.
            dialog.SetOptions(options | 0x2 | 0x4 | 0x40 | 0x800 | 0x10000);
            dialog.SetFileTypes(1, [new FilterSpec(extension.ToUpperInvariant() + " 文件", "*." + extension)]);
            dialog.SetFileTypeIndex(1);
            dialog.SetDefaultExtension(extension);
            dialog.SetFileName(name);
            dialog.SetTitle("导出运行统计");
            dialog.SetOkButtonLabel("导出");

            window = CreateWindowEx(0, "STATIC", "WinGlance save cancellation", 0, 0, 0, 0, 0, new nint(-3), 0, 0, 0);
            if (window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!SetWindowSubclass(window, callback, 1, 0)) throw new Win32Exception("Cannot initialize the save dialog cancellation channel.");
            subclassed = true;
            timer = SetTimer(window, 1, 100, 0);
            if (timer == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            nint callbackWindow = window;
            registration = token.Register(() => { bool posted = PostMessage(callbackWindow, CancelMessage, 0, 0); if (diagnostic is not null) Trace($"cancel-post:{posted}"); }, useSynchronizationContext: false);
            token.ThrowIfCancellationRequested();
            showing = true;
            Trace("show");
            int hr;
            try { hr = dialog.Show(owner); if (diagnostic is not null) Trace($"show-result:{hr:X8}"); }
            finally { showing = false; }
            token.ThrowIfCancellationRequested();
            if (hr != Cancelled)
            {
                Marshal.ThrowExceptionForHR(hr);
                dialog.GetResult(out result);
                result.GetDisplayName(0x80058000, out nint text); // SIGDN_FILESYSPATH
                try { selected = Marshal.PtrToStringUni(text) ?? throw new IOException("保存位置无效。"); }
                finally { Marshal.FreeCoTaskMem(text); }
                token.ThrowIfCancellationRequested();
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            finishing = true; showing = false;
            void Cleanup(Action action)
            {
                try { action(); }
                catch (Exception error) when (error is not OutOfMemoryException) { failure ??= error; }
            }
            // Callback only posts; it cannot depend on this STA/owner continuing to pump.
            Cleanup(registration.Dispose);
            if (timer != 0) Cleanup(() => KillTimer(window, timer));
            if (subclassed) Cleanup(() => RemoveWindowSubclass(window, callback, 1));
            if (window != 0) Cleanup(() => DestroyWindow(window));
            if (result is not null) Cleanup(() => Marshal.ReleaseComObject(result));
            if (dialog is not null) Cleanup(() => Marshal.ReleaseComObject(dialog));
            if (initialized) Cleanup(CoUninitialize);
            GC.KeepAlive(callback);
        }
        // No continuation observes completion until the modal window and STA resources are gone.
        if (failure is OperationCanceledException || token.IsCancellationRequested) completion.TrySetCanceled(token);
        else if (failure is not null) completion.TrySetException(failure);
        else completion.TrySetResult(selected);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private readonly struct FilterSpec(string name, string pattern)
    {
        [MarshalAs(UnmanagedType.LPWStr)] public readonly string Name = name;
        [MarshalAs(UnmanagedType.LPWStr)] public readonly string Pattern = pattern;
    }

    // Slots match IModalWindow + IFileDialog; unused slots are still declared in order.
    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(nint owner);
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] FilterSpec[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(nint events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out nint name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int placement);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        [PreserveSig] int Close(int result);
        void SetClientGuid(in Guid guid);
        void ClearClientData();
        void SetFilter(nint filter);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint context, in Guid handler, in Guid iid, out nint result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, out nint name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleWindow
    {
        void GetWindow(out nint window);
        void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
    }

    private delegate nint SubclassProcedure(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint model);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extendedStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    [DllImport("user32.dll", SetLastError = true)] private static extern nuint SetTimer(nint hwnd, nuint id, uint milliseconds, nint callback);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool KillTimer(nint hwnd, nuint id);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint hwnd, SubclassProcedure procedure, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProcedure procedure, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}
