using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Spark.Api;
using SparkStudio.Infrastructure;

namespace SparkStudio.Views;

public sealed record ConnectionDialogResult(string Url, ConnectionProfile Profile);

/// <summary>Edits the server URL and the device ID + token used for it.</summary>
public static class ConnectionDialog
{
    public static ConnectionDialogResult? Show(Window? owner, Settings settings, string currentUrl)
    {
        var w = DialogKit.NewWindow(owner, "Connection settings", 480);
        Brush Res(string key) => (Brush)Application.Current.FindResource(key);

        var url = new TextBox { Text = currentUrl };
        var deviceId = new TextBox();
        var token = new PasswordBox { Padding = new Thickness(6, 4, 6, 4), BorderBrush = Res("LineBrush") };
        var tokenHint = new TextBlock { Foreground = Res("MutedBrush"), FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        var removeToken = new CheckBox { Content = "Remove the saved token", Margin = new Thickness(0, 6, 0, 0) };
        var plaintextWarning = new Border
        {
            Background = Res("WarnBrush"), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 10, 0, 0), CornerRadius = new CornerRadius(4),
            Child = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "This URL uses plain http, so the token would travel unencrypted. Use the server's https:// address for token-protected servers.",
            },
        };
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        var test = new Button { Content = "Test", Style = (Style)Application.Current.FindResource("FlatButton"), HorizontalAlignment = HorizontalAlignment.Left };

        void LoadProfile()
        {
            var p = settings.ProfileFor(url.Text);
            deviceId.Text = p.DeviceId;
            token.Password = "";
            removeToken.IsChecked = false;
            removeToken.Visibility = p.HasToken ? Visibility.Visible : Visibility.Collapsed;
            tokenHint.Text = p.HasToken
                ? "A token is saved for this server (encrypted for your Windows account). Leave blank to keep it, or paste a new one."
                : "Paste the device token issued for this device ID. Leave blank for open servers.";
        }

        bool WillHaveToken() => token.Password.Length > 0 || (settings.ProfileFor(url.Text).HasToken && removeToken.IsChecked != true);

        void UpdateWarning()
        {
            var plaintext = Uri.TryCreate(url.Text.Trim(), UriKind.Absolute, out var u) && ConnectionCheck.IsPlaintext(u);
            plaintextWarning.Visibility = plaintext && WillHaveToken() ? Visibility.Visible : Visibility.Collapsed;
        }

        string? TokenToUse() => token.Password.Length > 0 ? token.Password.Trim()
            : removeToken.IsChecked == true ? null : settings.ProfileFor(url.Text).GetToken();

        url.LostFocus += (_, _) => { LoadProfile(); UpdateWarning(); };
        token.PasswordChanged += (_, _) => UpdateWarning();
        removeToken.Checked += (_, _) => UpdateWarning();
        removeToken.Unchecked += (_, _) => UpdateWarning();

        test.Click += async (_, _) =>
        {
            if (!TryValidate(url.Text, deviceId.Text, out var uri, out var error))
            {
                result.Foreground = Res("DangerBrush");
                result.Text = error;
                return;
            }
            test.IsEnabled = false;
            result.Foreground = Res("MutedBrush");
            result.Text = "Checking…";
            var tok = TokenToUse();
            using var client = new SparkClient(uri!, tok);
            var check = await ConnectionCheck.RunAsync(client, deviceId.Text.Trim(), tok != null);
            result.Foreground = check.Ok ? Res("TextBrush") : Res("DangerBrush");
            result.Text = check.Headline + "\n" + check.Details;
            test.IsEnabled = true;
        };

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(DialogKit.Label("Server URL"));
        root.Children.Add(url);
        root.Children.Add(new Border { Height = 10 });
        root.Children.Add(DialogKit.Label("Device ID"));
        root.Children.Add(deviceId);
        root.Children.Add(new TextBlock
        {
            Text = "Must match the device the token was issued for (letters, digits, _ and -).",
            Foreground = Res("MutedBrush"), FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
        });
        root.Children.Add(new Border { Height = 10 });
        root.Children.Add(DialogKit.Label("Device token"));
        root.Children.Add(token);
        root.Children.Add(tokenHint);
        root.Children.Add(removeToken);
        root.Children.Add(plaintextWarning);
        root.Children.Add(new Border { Height = 12 });
        root.Children.Add(test);
        root.Children.Add(result);
        root.Children.Add(DialogKit.Buttons(w, out _, "Save & connect", validate: () =>
        {
            if (TryValidate(url.Text, deviceId.Text, out _, out var error)) return true;
            result.Foreground = Res("DangerBrush");
            result.Text = error;
            return false;
        }));
        w.Content = root;

        LoadProfile();
        UpdateWarning();
        w.Loaded += (_, _) => (string.IsNullOrEmpty(deviceId.Text) ? deviceId : (Control)token).Focus();

        if (w.ShowDialog() != true) return null;

        var profile = settings.ProfileFor(url.Text);
        var saved = new ConnectionProfile { DeviceId = deviceId.Text.Trim(), ProtectedToken = profile.ProtectedToken };
        if (token.Password.Length > 0) saved.SetToken(token.Password);
        else if (removeToken.IsChecked == true) saved.SetToken(null);
        return new ConnectionDialogResult(url.Text.Trim(), saved);
    }

    private static bool TryValidate(string url, string deviceId, out Uri? uri, out string error)
    {
        uri = null;
        error = "";
        if (!Uri.TryCreate(url.Trim().TrimEnd('/') + "/", UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            error = "Enter the server's http:// or https:// address.";
            return false;
        }
        if (!Settings.IsValidDeviceId(deviceId.Trim()))
        {
            error = "Device IDs are 1–64 letters, digits, _ or -.";
            return false;
        }
        return true;
    }
}
