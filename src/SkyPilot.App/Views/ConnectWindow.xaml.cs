using System.Windows;
using SkyPilot.Core.Session;
using SkyPilot.Core.Settings;

namespace SkyPilot.App.Views;

public partial class ConnectWindow : Window
{
    private readonly AppSettings _settings;

    public ConnectWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        CallsignBox.Text = settings.LastCallsign;
        TypeBox.Text = settings.LastTypeCode;
        ServerBox.ItemsSource = settings.Servers;
        ServerBox.SelectedItem = settings.CurrentServer;
        Loaded += (_, _) => CallsignBox.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string callsign = CallsignBox.Text.Trim().ToUpperInvariant();
        string type = TypeBox.Text.Trim().ToUpperInvariant();
        if (!NetworkSession.IsValidCallsign(callsign))
        {
            ErrorText.Text = "Позывной: 2–12 латинских букв и цифр";
            return;
        }
        if (type.Length is < 2 or > 4)
        {
            ErrorText.Text = "Укажите ICAO-код типа ВС";
            return;
        }
        _settings.LastCallsign = callsign;
        _settings.LastTypeCode = type;
        if (ServerBox.SelectedItem is ServerEntry server) _settings.SelectedServer = server.Name;
        DialogResult = true;
    }
}
