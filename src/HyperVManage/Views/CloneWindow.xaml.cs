using System.Windows;
using System.Windows.Automation;
using HyperVManage.Helpers;
using HyperVManage.Models;
using HyperVManage.Services;
using HyperVManage.ViewModels;

namespace HyperVManage.Views;

/// <summary>Asks what to call a clone and where to keep it. Problems are shown in the dialog, which
/// stays open, rather than after it has closed.</summary>
public partial class CloneWindow : Window
{
    /// <summary>Asks for a folder, starting from the one given; null if cancelled. The folder
    /// dialog, or a stand-in in tests.</summary>
    internal Func<string, string?> PickFolder { get; set; }

    /// <summary>Checks a location; swapped in tests, which can't count on the drives.</summary>
    internal Func<string, string?> CheckLocation { get; set; } = l => NewVmScript.LocationProblem(l);

    internal CloneWindow(VmInfo vm, string location)
    {
        InitializeComponent();
        PickFolder = start => FolderPicker.Pick(this, start);
        Title = $"Clone {vm.Name}";
        NameBox.Text = $"{vm.Name}-copy";
        LocationBox.Text = location;
        NoteText.Text = "The copy has its own disk, so this takes a few minutes and needs as much free space as the VM uses, " +
            "twice over while it's being made. Windows inside it keeps the same computer name: rename it there before running both on your network.";
        // Focus goes straight to the name, so the note would never be read unless it is the box's
        // description: screen readers speak that after its name.
        AutomationProperties.SetHelpText(NameBox, NoteText.Text);
        ShowWhere();
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    public CloneRequest? Result { get; private set; }

    public static CloneRequest? Ask(Window owner, VmInfo vm, string location)
    {
        var w = new CloneWindow(vm, location) { Owner = owner };
        return w.ShowDialog() == true ? w.Result : null;
    }

    private void ShowWhere()
    {
        WhereText.Text = LocationText.Describe(LocationBox.Text, NameBox.Text, "The copy");
        AutomationProperties.SetHelpText(LocationBox, WhereText.Text);
    }

    private void Inputs_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (IsInitialized && WhereText is not null) ShowWhere();
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder(LocationBox.Text) is { } folder) LocationBox.Text = folder;
        // Focus goes to the box, which then reads the folder and, as its description, where the copy
        // will go; announcing that as well would say it twice.
        LocationBox.Focus();
    }

    internal void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var location = LocationText.Normalize(LocationBox.Text);
        if (NewVmScript.NameProblem(name) is { } nameProblem) { Refuse(nameProblem, NameBox); return; }
        if (CheckLocation(location) is { } locationProblem) { Refuse(locationProblem, LocationBox); return; }
        Result = new CloneRequest(name, location);
        DialogResult = true;
    }

    private void Refuse(string problem, System.Windows.Controls.TextBox box)
    {
        ErrorText.Text = problem;
        ErrorText.Visibility = Visibility.Visible;
        box.Focus();
        box.SelectAll();
        Announcer.Announce(this, problem);
    }
}
