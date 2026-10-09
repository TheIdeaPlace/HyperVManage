using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace HyperVManage.Services;

public static class Browser
{
    /// <summary>
    /// Opens a page in the user's browser, as the user. The app runs elevated, and a browser
    /// started from it directly would run elevated too, so the desktop's own shell, which runs as
    /// the user, is asked to open it. Explorer's command line can't do it: given an address with
    /// a query (?a=b) it opens the Documents folder instead, which is how GitHub's new issue form
    /// first failed in the test VM.
    /// </summary>
    public static void Open(Uri page)
    {
        if (page.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("Only https pages are opened.", nameof(page));
        if (!IsElevated())
        {
            Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            return;
        }
        try
        {
            DesktopShellExecute(page.AbsoluteUri);
        }
        catch (Exception) when (page.Query.Length == 0)
        {
            // No desktop shell to ask (Explorer not running): Explorer's command line still
            // opens a plain address.
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            {
                UseShellExecute = false,
                ArgumentList = { page.AbsoluteUri },
            })?.Dispose();
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // Raymond Chen, "How can I launch an unelevated process from my elevated process, redux":
    // find the desktop's folder view through the shell windows, and call ShellExecute on its
    // Application object, which runs inside Explorer.
    private static void DesktopShellExecute(string address)
    {
        var shellWindows = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), throwOnError: true)!)!;
        object? desktop = null, view = null, folderView = null, application = null;
        try
        {
            const int CSIDL_DESKTOP = 0, SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1;
            var args = new object?[] { CSIDL_DESKTOP, null, SWC_DESKTOP, 0, SWFO_NEEDDISPATCH };
            var byRef = new ParameterModifier(5);
            byRef[0] = true;
            byRef[1] = true;
            byRef[3] = true;
            desktop = shellWindows.GetType().InvokeMember("FindWindowSW", BindingFlags.InvokeMethod, null, shellWindows, args, [byRef], null, null)
                      ?? throw new InvalidOperationException("The desktop isn't running.");

            var topLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
            var shellBrowserId = typeof(IShellBrowser).GUID;
            Marshal.ThrowExceptionForHR(((IServiceProvider)desktop).QueryService(ref topLevelBrowser, ref shellBrowserId, out var browser));
            Marshal.ThrowExceptionForHR(((IShellBrowser)browser).QueryActiveShellView(out var shellView));
            view = shellView;
            var dispatch = new Guid("00020400-0000-0000-C000-000000000046");
            const uint SVGIO_BACKGROUND = 0;
            Marshal.ThrowExceptionForHR(((IShellView)shellView).GetItemObject(SVGIO_BACKGROUND, ref dispatch, out folderView));

            application = folderView.GetType().InvokeMember("Application", BindingFlags.GetProperty, null, folderView, null)!;
            const int SW_SHOWNORMAL = 1;
            application.GetType().InvokeMember("ShellExecute", BindingFlags.InvokeMethod, null, application,
                [address, "", "", "open", SW_SHOWNORMAL]);
        }
        finally
        {
            foreach (var o in new[] { application, folderView, view, desktop, shellWindows })
                if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
        }
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }

    // Only the method called is real; the others hold their places in the interface's table.
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow();
        void ContextSensitiveHelp();
        void InsertMenusSB();
        void SetMenuSB();
        void RemoveMenusSB();
        void SetStatusTextSB();
        void EnableModelessSB();
        void TranslateAcceleratorSB();
        void BrowseObject();
        void GetViewStateStream();
        void GetControlWindow();
        void SendControlMsg();
        [PreserveSig]
        int QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow();
        void ContextSensitiveHelp();
        void TranslateAccelerator();
        void EnableModeless();
        void UIActivate();
        void Refresh();
        void CreateViewWindow();
        void DestroyViewWindow();
        void GetCurrentInfo();
        void AddPropertySheetPages();
        void SaveViewState();
        void SelectItem();
        [PreserveSig]
        int GetItemObject(uint item, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }
}
