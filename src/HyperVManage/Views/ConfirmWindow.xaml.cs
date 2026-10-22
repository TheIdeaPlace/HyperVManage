using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace HyperVManage.Views;

/// <summary>
/// A Yes or No question. Used instead of a message box, which with only Yes and No ignores
/// Escape and has no close button: here Escape, Alt+F4 and the close button all answer No.
/// </summary>
public partial class ConfirmWindow : Window
{
    private ConfirmWindow() => InitializeComponent();

    /// <param name="yesFirst">Start on Yes, for a question whose usual answer is yes; otherwise
    /// No has focus and Enter, so a destructive one is never a single key press away.</param>
    public static bool Ask(Window owner, string question, string title, bool yesFirst = false)
    {
        var w = Create(question, title, yesFirst);
        w.Owner = owner;
        return w.ShowDialog() == true;
    }

    internal static ConfirmWindow Create(string question, string title, bool yesFirst = false)
    {
        var w = new ConfirmWindow { Title = title };
        w.QuestionText.Text = question;
        var start = yesFirst ? w.YesButton : w.NoButton;
        start.IsDefault = true;
        // Focus goes to a button, so the question would never be read unless it is that button's
        // description: screen readers speak that after the button's name. Only that one, so
        // tabbing to the other doesn't read it all again; Shift+Tab to the text reads it there.
        AutomationProperties.SetHelpText(start, question);
        // Also the window's remembered focus, so activating it, however late, comes back to the
        // button and never to the question's text.
        FocusManager.SetFocusedElement(w, start);
        w.Loaded += (_, _) => start.Focus();
        return w;
    }

    private void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
