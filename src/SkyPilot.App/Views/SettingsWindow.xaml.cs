using System.Windows;
using SkyPilot.Core.Settings;
using SkyPilot.Core.Web;

namespace SkyPilot.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ISecretProtector _protector;

    public SettingsWindow(AppSettings settings, ISecretProtector protector)
    {
        InitializeComponent();
        _settings = settings;
        _protector = protector;
        CidBox.Text = settings.Cid > 0 ? settings.Cid.ToString() : "";
        PasswordBox.Password = protector.Unprotect(settings.ProtectedPassword);
        NameBox.Text = settings.RealName;
        var server = settings.CurrentServer;
        ServerBox.Text = $"{server.Host}:{server.Port}";
        WebsiteBox.Text = settings.Website;
        SoundBox.IsChecked = settings.PlaySoundOnPrivateMessage;
        TopmostBox.IsChecked = settings.KeepWindowOnTop;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(CidBox.Text.Trim(), out var cid) || cid <= 0)
        {
            ErrorText.Text = "CID — положительное число";
            return;
        }
        var parts = ServerBox.Text.Trim().Split(':');
        int port = 6809;
        if (parts[0].Length == 0 || parts.Length > 2 || parts.Length == 2 && !int.TryParse(parts[1], out port))
        {
            ErrorText.Text = "Адрес сервера: хост или хост:порт, например 127.0.0.1:6809";
            return;
        }
        string website = WebsiteBox.Text.Trim();
        if (website.Length > 0 && !WebsiteClient.TryParseSite(website, out _))
        {
            ErrorText.Text = "Адрес сайта: например skynetwork.example или http://127.0.0.1:8000";
            return;
        }
        _settings.Cid = cid;
        _settings.Website = website;
        _settings.ProtectedPassword = _protector.Protect(PasswordBox.Password);
        _settings.RealName = NameBox.Text.Trim();
        var server = _settings.CurrentServer;
        if (!_settings.Servers.Contains(server)) _settings.Servers.Add(server);
        server.Host = parts[0];
        server.Port = port;
        _settings.SelectedServer = server.Name;
        _settings.PlaySoundOnPrivateMessage = SoundBox.IsChecked == true;
        _settings.KeepWindowOnTop = TopmostBox.IsChecked == true;
        DialogResult = true;
    }
}
