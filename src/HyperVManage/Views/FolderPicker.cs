using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace HyperVManage.Views;

/// <summary>Windows' own folder dialog, for choosing where a VM is kept.</summary>
public static class FolderPicker
{
    /// <param name="start">The folder to open in: the one in the Location box, or the nearest part of
    /// it that exists; empty for the dialog's own choice.</param>
    public static string? Pick(Window? owner, string start)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where to keep the virtual machine" };
        if (Existing(Services.LocationText.Normalize(start)) is { } folder) dialog.InitialDirectory = folder;
        var chosen = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return chosen == true ? dialog.FolderName : null;
    }

    private static string? Existing(string path)
    {
        try
        {
            for (var p = path; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
                if (Directory.Exists(p)) return p;
        }
        catch (Exception) { }
        return null;
    }
}
