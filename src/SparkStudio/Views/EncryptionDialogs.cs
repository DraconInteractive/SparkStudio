using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Spark.Notes.Crypto;

namespace SparkStudio.Views;

/// <summary>Chooses a passphrase: for encrypting a project, or a new one for an encrypted project.</summary>
public static class PassphraseDialog
{
    public static string? Show(Window? owner, string title, string intro, string okText)
    {
        var w = DialogKit.NewWindow(owner, title, 460);
        var first = new PasswordBox();
        var second = new PasswordBox();
        var error = new TextBlock { Foreground = (Brush)Application.Current.FindResource("DangerBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        root.Children.Add(DialogKit.Label($"Passphrase (at least {ProjectVault.MinPassphraseLength} characters; a few unrelated words works well)"));
        root.Children.Add(first);
        root.Children.Add(new Border { Height = 10 });
        root.Children.Add(DialogKit.Label("Type it again"));
        root.Children.Add(second);
        root.Children.Add(error);
        root.Children.Add(DialogKit.Buttons(w, out _, okText, () =>
        {
            string? problem = first.Password.Length < ProjectVault.MinPassphraseLength
                ? $"Use at least {ProjectVault.MinPassphraseLength} characters."
                : first.Password != second.Password ? "The two passphrases don't match." : null;
            error.Text = problem ?? "";
            error.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
            return problem == null;
        }));
        w.Content = root;
        w.Loaded += (_, _) => first.Focus();
        return w.ShowDialog() == true ? first.Password : null;
    }
}

/// <summary>Shows a new recovery key once, and makes the user confirm they've stored it.</summary>
public static class RecoveryKeyDialog
{
    public static void Show(Window? owner, string projectName, string code)
    {
        var w = DialogKit.NewWindow(owner, "Recovery key", 480);
        var box = new TextBox
        {
            Text = code, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 16,
            TextWrapping = TextWrapping.Wrap, Padding = new Thickness(8), HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var copy = new Button { Content = "Copy", Style = (Style)Application.Current.FindResource("FlatButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(code); copy.Content = "Copied"; } catch (Exception) { }
        };
        var saved = new CheckBox { Content = "I've stored the recovery key somewhere safe", Margin = new Thickness(0, 14, 0, 0) };

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            Text = $"This recovery key opens \"{projectName}\" if the passphrase is forgotten. It's shown only now. " +
                   "Keep it somewhere safe and separate from this PC, such as a password manager or on paper.\n\n" +
                   "Without the passphrase or this key, the encrypted notes can't be recovered by anyone, including the server's administrator.",
        });
        root.Children.Add(box);
        root.Children.Add(copy);
        root.Children.Add(saved);
        var buttons = DialogKit.Buttons(w, out var ok, "Done");
        buttons.Children.RemoveAt(0); // no Cancel: the vault already exists
        ok.IsEnabled = false;
        saved.Checked += (_, _) => ok.IsEnabled = true;
        saved.Unchecked += (_, _) => ok.IsEnabled = false;
        root.Children.Add(buttons);
        w.Content = root;
        w.Closing += (_, e) => e.Cancel = saved.IsChecked != true;
        w.ShowDialog();
    }
}

/// <summary>Unlocks an encrypted project with its passphrase (or recovery key). Returns the project key.</summary>
public static class UnlockDialog
{
    public sealed record Result(byte[] Key, bool Remember);

    public static Result? Show(Window? owner, string projectName, VaultFile vault, bool rememberByDefault = true)
    {
        var w = DialogKit.NewWindow(owner, "Unlock project", 420);
        var pass = new PasswordBox();
        var recovery = new TextBox { Visibility = Visibility.Collapsed, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
        var label = DialogKit.Label("Passphrase");
        var toggle = new TextBlock
        {
            Text = "Use the recovery key instead", Margin = new Thickness(0, 6, 0, 0), Cursor = Cursors.Hand,
            Foreground = (Brush)Application.Current.FindResource("AccentBrush"),
        };
        bool useRecovery = false;
        toggle.MouseLeftButtonUp += (_, _) =>
        {
            useRecovery = !useRecovery;
            pass.Visibility = useRecovery ? Visibility.Collapsed : Visibility.Visible;
            recovery.Visibility = useRecovery ? Visibility.Visible : Visibility.Collapsed;
            label.Text = useRecovery ? "Recovery key" : "Passphrase";
            toggle.Text = useRecovery ? "Use the passphrase instead" : "Use the recovery key instead";
            (useRecovery ? (Control)recovery : pass).Focus();
        };
        var remember = new CheckBox
        {
            Content = "Remember on this PC (until you lock the project)", IsChecked = rememberByDefault, Margin = new Thickness(0, 14, 0, 0),
            ToolTip = "Keeps the project key on this PC, encrypted for your Windows account, so you aren't asked again.",
        };
        var error = new TextBlock { Foreground = (Brush)Application.Current.FindResource("DangerBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };

        byte[]? key = null;
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = $"\"{projectName}\" is encrypted. Enter its passphrase to read and edit its notes.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        root.Children.Add(label);
        root.Children.Add(pass);
        root.Children.Add(recovery);
        root.Children.Add(toggle);
        root.Children.Add(remember);
        root.Children.Add(error);
        root.Children.Add(DialogKit.Buttons(w, out _, "Unlock", () =>
        {
            var old = w.Cursor;
            w.Cursor = Cursors.Wait;
            try
            {
                key = useRecovery ? ProjectVault.UnlockWithRecovery(vault, recovery.Text) : ProjectVault.Unlock(vault, pass.Password);
                return true;
            }
            catch (Exception e) when (e is WrongPassphraseException or NotSupportedException)
            {
                error.Text = e.Message;
                error.Visibility = Visibility.Visible;
                if (!useRecovery) pass.SelectAll();
                return false;
            }
            finally { w.Cursor = old; }
        }));
        w.Content = root;
        w.Loaded += (_, _) => pass.Focus();
        return w.ShowDialog() == true && key != null ? new Result(key, remember.IsChecked == true) : null;
    }
}
