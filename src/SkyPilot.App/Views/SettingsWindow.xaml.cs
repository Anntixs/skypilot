using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SkyNetwork.Voice;
using SkyPilot.Core.Settings;
using SkyPilot.Core.Web;

namespace SkyPilot.App.Views;

public partial class SettingsWindow : Window
{
    private const string DefaultDevice = "Default";

    private readonly AppSettings _settings;
    private readonly ISecretProtector _protector;
    private readonly Func<float> _micLevel;
    private readonly DispatcherTimer _meter = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private PttBinding _ptt;
    private CancellationTokenSource? _capture;

    public SettingsWindow(AppSettings settings, ISecretProtector protector, Func<float> micLevel)
    {
        InitializeComponent();
        _settings = settings;
        _protector = protector;
        _micLevel = micLevel;
        CidBox.Text = settings.Cid > 0 ? settings.Cid.ToString() : "";
        PasswordBox.Password = protector.Unprotect(settings.ProtectedPassword);
        NameBox.Text = settings.RealName;
        var server = settings.CurrentServer;
        ServerBox.Text = $"{server.Host}:{server.Port}";
        WebsiteBox.Text = settings.Website;
        SoundBox.IsChecked = settings.PlaySoundOnPrivateMessage;
        TopmostBox.IsChecked = settings.KeepWindowOnTop;

        var (inputs, outputs) = VoiceClient.Devices();
        FillDevices(InputBox, inputs, settings.InputDevice);
        FillDevices(OutputBox, outputs, settings.OutputDevice);
        MicGainSlider.Value = Math.Round(settings.MicGain * 100);
        VolumeSlider.Value = Math.Round(settings.OutputVolume * 100);
        OnSliderChanged(this, new RoutedPropertyChangedEventArgs<double>(0, 0));
        _ptt = PttBinding.Parse(settings.PttKey);
        PttBox.Text = Describe(_ptt);
        VoicePortBox.Text = settings.VoicePort.ToString();

        _meter.Tick += (_, _) => MicMeter.Value = _micLevel();
        _meter.Start();
        // While waiting for the PTT key, keys must not press buttons (Enter = save, Esc = cancel).
        PreviewKeyDown += (_, e) => e.Handled |= _capture != null;
        Closed += (_, _) =>
        {
            _meter.Stop();
            _capture?.Cancel();
        };
    }

    /// <summary>"Default" and the devices; a saved device that is unplugged stays in the list.</summary>
    private static void FillDevices(ComboBox box, IReadOnlyList<string> devices, string selected)
    {
        box.Items.Add(DefaultDevice);
        foreach (var d in devices) box.Items.Add(d);
        if (selected.Length > 0 && !devices.Contains(selected)) box.Items.Add(selected);
        box.SelectedIndex = selected.Length > 0 ? box.Items.IndexOf(selected) : 0;
    }

    private static string SelectedDevice(ComboBox box) =>
        box.SelectedIndex > 0 && box.SelectedItem is string name ? name : "";

    /// <summary>The PTT control in English ("not assigned", "Joystick 1, button 5", or the key name).</summary>
    private static string Describe(PttBinding b) => b.Kind switch
    {
        PttKind.None => "not assigned",
        PttKind.Joystick => $"Joystick {b.Device + 1}, button {b.Code + 1}",
        _ => b.Code switch
        {
            0x04 => "Middle mouse button",
            0x05 => "Mouse button 4",
            0x06 => "Mouse button 5",
            _ => b.Describe(),
        },
    };

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MicGainText != null) MicGainText.Text = $"{MicGainSlider.Value:0} %";
        if (VolumeText != null) VolumeText.Text = $"{VolumeSlider.Value:0} %";
    }

    private async void OnPttCaptureClick(object sender, RoutedEventArgs e)
    {
        if (_capture != null)
        {
            _capture.Cancel();
            return;
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _capture = cts;
        PttCaptureButton.Content = "CANCEL";
        PttBox.Text = "Press a key or joystick button…";
        try
        {
            // Polled off the UI thread so the window stays responsive.
            var binding = await Task.Run(() => PushToTalk.CaptureAsync(cts.Token));
            const int escape = 0x1B;
            if (binding.Kind != PttKind.None && binding != new PttBinding(PttKind.Keyboard, escape)) _ptt = binding;
        }
        catch (OperationCanceledException)
        {
            // Cancelled, timed out or the window was closed: keep the old key.
        }
        finally
        {
            _capture = null;
            PttCaptureButton.Content = "ASSIGN";
            PttBox.Text = Describe(_ptt);
        }
    }

    private void OnPttClearClick(object sender, RoutedEventArgs e)
    {
        _capture?.Cancel();
        _ptt = PttBinding.None;
        PttBox.Text = Describe(_ptt);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(CidBox.Text.Trim(), out var cid) || cid <= 0)
        {
            ErrorText.Text = "CID must be a positive number";
            return;
        }
        var parts = ServerBox.Text.Trim().Split(':');
        int port = 6809;
        if (parts[0].Length == 0 || parts.Length > 2 || parts.Length == 2 && !int.TryParse(parts[1], out port))
        {
            ErrorText.Text = "Server address: host or host:port, e.g. 127.0.0.1:6809";
            return;
        }
        string website = WebsiteBox.Text.Trim();
        if (website.Length > 0 && !WebsiteClient.TryParseSite(website, out _))
        {
            ErrorText.Text = "Website address: e.g. skynetwork.example or http://127.0.0.1:8000";
            return;
        }
        if (!int.TryParse(VoicePortBox.Text.Trim(), out var voicePort) || voicePort is <= 0 or > 65535)
        {
            ErrorText.Text = "Voice server port must be a number from 1 to 65535 (default 3782)";
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
        _settings.InputDevice = SelectedDevice(InputBox);
        _settings.OutputDevice = SelectedDevice(OutputBox);
        _settings.MicGain = MicGainSlider.Value / 100;
        _settings.OutputVolume = VolumeSlider.Value / 100;
        _settings.PttKey = _ptt.ToString();
        _settings.VoicePort = voicePort;
        DialogResult = true;
    }
}
