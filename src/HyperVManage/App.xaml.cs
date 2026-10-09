using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using HyperVManage.Services;
using HyperVManage.ViewModels;
using HyperVManage.Views;
using Velopack;

namespace HyperVManage;

public partial class App : Application
{
    // Velopack first, before WPF: when Setup installs, updates or uninstalls the app it runs it
    // with an argument of its own, does its work here and ends the process. On a normal start it
    // does nothing.
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    private IUpdateService? _updates;

    protected override void OnExit(ExitEventArgs e)
    {
        // Arms installing an update downloaded while the app ran.
        _updates?.Dispose();
        base.OnExit(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --demo: pretend VMs, no Hyper-V, no administrator rights. For trying the app out and
        // for checking the UI on a machine without Hyper-V.
        var demo = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase);

        if (!demo)
        {
            if (!IsAdministrator())
            {
                // Hyper-V's cmdlets need administrator rights, and so does building a VM's disk.
                // Ask once, the way New-HyperVRdpVM.ps1 does, and carry on in the elevated copy.
                if (!RelaunchElevated(e.Args))
                {
                    MessageBox.Show(
                        "Hyper-V Manage needs administrator rights to manage virtual machines. " +
                        "Run it again and choose Yes when Windows asks.",
                        "Hyper-V Manage", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                Shutdown();
                return;
            }
            if (!HyperVAvailable())
            {
                MessageBox.Show(
                    "Hyper-V isn't turned on on this PC, or this edition of Windows doesn't include it. " +
                    "On Windows Pro, Enterprise or Education, run this in an administrator PowerShell window and restart:\n\n" +
                    "Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All",
                    "Hyper-V Manage", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }

        IHyperVService service = demo ? new DemoHyperVService() : new PowerShellHyperVService();
        var vm = new MainViewModel(service, demo ? new InMemoryCredentialStore() : new WindowsCredentialStore());
        // --update-feed <folder or address>: Velopack packages to update from instead of GitHub,
        // for trying an update without publishing one.
        _updates = new UpdateService(ArgumentAfter(e.Args, "--update-feed"));
        var window = new MainWindow(vm, demo, new UpdateChecker(_updates), new BugReportService(demo));
        MainWindow = window;
        window.Show();
    }

    private static string? ArgumentAfter(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool IsAdministrator() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static bool RelaunchElevated(string[] args)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return false;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception)
        {
            // The user chose No at the permission prompt.
            return false;
        }
    }

    // The Hyper-V PowerShell module ships with the Hyper-V feature; its folder is the cheapest check.
    private static bool HyperVAvailable() =>
        System.IO.Directory.Exists(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "Modules", "Hyper-V"));
}
