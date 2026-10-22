using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HyperVManage.Views;

/// <summary>
/// A Yes or No question. Used instead of a message box, which with only Yes and No ignores
/// Escape and has no close button: here Escape, Alt+F4 and the close button all answer No.
/// Unlike a message box, which disables only its owner, it disables every window of the app
/// while it's open, New Virtual Machine and the screenshot viewers included.
/// </summary>
public partial class ConfirmWindow : Window
{
    private ConfirmWindow() => InitializeComponent();

    /// <param name="yesFirst">Start on Yes, for a question whose usual answer is yes; otherwise
    /// No has focus and Enter, so a destructive one is never a single key press away.</param>
    /// <param name="warning">It deletes or stops something: the warning icon and sound, as a
    /// warning message box has.</param>
    public static bool Ask(Window owner, string question, string title, bool yesFirst = false, bool warning = false)
    {
        var w = Create(question, title, yesFirst, warning);
        w.Owner = owner;
        return w.ShowDialog() == true;
    }

    internal static ConfirmWindow Create(string question, string title, bool yesFirst = false, bool warning = false)
    {
        var w = new ConfirmWindow { Title = title };
        w.QuestionText.Text = question;
        if (warning)
        {
            // Windows' own warning icon, the one a warning message box shows. Shared: never destroyed.
            w.WarningIcon.Source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                LoadIcon(IntPtr.Zero, IdiWarning), Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            w.WarningIcon.Visibility = Visibility.Visible;
            w.ContentRendered += (_, _) => System.Media.SystemSounds.Exclamation.Play();
        }
        var start = yesFirst ? w.YesButton : w.NoButton;
        start.IsDefault = true;
        // The buttons are their on-screen text and nothing more. The question is heard as the
        // dialog's text when it opens, as with a message box.
        // Also the window's remembered focus, so activating it, however late, comes back to the
        // button.
        FocusManager.SetFocusedElement(w, start);
        w.Loaded += (_, _) => start.Focus();
        return w;
    }

    private static readonly IntPtr IdiWarning = 32515;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    private void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
