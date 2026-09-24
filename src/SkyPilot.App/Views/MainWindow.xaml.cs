using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SkyPilot.App.Services;
using SkyPilot.App.ViewModels;
using SkyPilot.Core.Fsd;
using SkyPilot.Core.Matching;
using SkyPilot.Core.Model;
using SkyPilot.Core.Session;
using SkyPilot.Core.Settings;
using SkyPilot.Core.Voice;
using SkyPilot.Core.Web;
using SkyPilot.SimConnect;

namespace SkyPilot.App.Views;

public partial class MainWindow : Window
{
    private static readonly TimeSpan FlightPlanPollInterval = TimeSpan.FromSeconds(30);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly string _settingsPath = Path.Combine(AppSettings.DefaultDirectory, "settings.json");
    private readonly DpapiProtector _protector = new();
    private readonly AppSettings _settings;
    private readonly MainViewModel _vm = new();
    private readonly MsfsSimulator _sim = new();
    private readonly NetworkSession _session;
    private readonly CommandProcessor _commands;
    private readonly PilotVoice _voice = new();
    private readonly DispatcherTimer _simRetry = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<string> _history = [];
    private int _historyIndex;
    private DateTime _nextPlanPoll;
    private FlightPlan? _sentPlan;
    private ConnectInfo? _connectInfo;
    private OwnAircraftData? _own;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _settings = AppSettings.Load(_settingsPath);
        _vm.Topmost = _settings.KeepWindowOnTop;

        var matcher = ModelMatcher.Load(Path.Combine(AppSettings.DefaultDirectory, "model-matching.json"));
        _session = new NetworkSession(_sim, matcher);
        _commands = new CommandProcessor(_session, _sim);

        _sim.ConnectionChanged += (_, connected) => Ui(() => _vm.SimConnected = connected);
        _sim.OwnAircraftUpdated += (_, own) => Ui(() => OnOwnAircraft(own));
        _session.ConnectionChanged += (_, connected) => Ui(() => OnNetworkConnectionChanged(connected));
        _session.MessageReceived += (_, m) => Ui(() => OnMessage(m));
        _session.ControllersChanged += (_, _) => Ui(() => _vm.SetControllers(_session.Controllers));
        _session.TrafficChanged += (_, _) => Ui(() => _vm.TrafficCount = _session.Traffic.Count);

        _voice.ApplySettings(_settings);
        _voice.Changed += (_, _) => Ui(UpdateVoiceStatus);
        _voice.Info += (_, text) => Ui(() => Info(text));
        _voice.Error += (_, text) => Ui(() => Error(text));

        _simRetry.Tick += (_, _) =>
        {
            TryConnectSim();
            _vm.Identing = _session.IsIdenting;
        };
        _simRetry.Start();
        TryConnectSim();

        _clock.Tick += async (_, _) =>
        {
            _vm.UtcTime = DateTime.UtcNow.ToString("HH:mm");
            if (_session.IsConnected && DateTime.UtcNow >= _nextPlanPoll) await RefreshFlightPlanAsync(quiet: true);
        };
        _vm.UtcTime = DateTime.UtcNow.ToString("HH:mm");
        _clock.Start();

        _vm.RadioTab.Add(new ChatMessage(MessageKind.Info, "SkyPilot",
            "Добро пожаловать в SkyPilot! Запустите MSFS, затем нажмите OFFLINE, чтобы подключиться. Команды: .help", DateTime.UtcNow));
        Closing += (_, _) =>
        {
            _settings.KeepWindowOnTop = _vm.Topmost;
            _settings.Save(_settingsPath);
            _voice.Dispose();
            // Send the logoff packet before the process exits (the core never resumes on the UI thread).
            _session.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            _sim.Dispose();
        };
    }

    private void Ui(Action action) => Dispatcher.BeginInvoke(action);

    private void Info(string text) => _vm.RadioTab.Add(new ChatMessage(MessageKind.Info, "SkyPilot", text, DateTime.UtcNow));
    private void Error(string text) => _vm.RadioTab.Add(new ChatMessage(MessageKind.Error, "SkyPilot", text, DateTime.UtcNow));

    private void TryConnectSim()
    {
        if (_sim.IsConnected) return;
        _sim.Connect();
        if (_sim.LastError != null && _vm.SimError == null) Error(_sim.LastError);
        _vm.SimError = _sim.LastError;
    }

    private async void OnNetworkConnectionChanged(bool connected)
    {
        _vm.NetConnected = connected;
        _vm.Callsign = _session.Callsign;
        _sentPlan = null;
        // Voice follows the network connection; its failures are only reported, never disconnect FSD.
        if (connected && _connectInfo is { } info)
            _voice.Start(new VoiceLogin(info.Host, _settings.VoicePort, info.Cid, _session.Callsign, info.Password));
        else
            _voice.Stop();
        if (connected) await RefreshFlightPlanAsync(quiet: true);
    }

    private void OnOwnAircraft(OwnAircraftData own)
    {
        _own = own;
        _vm.UpdateRadios(own);
        UpdateVoiceRadios();
        _voice.UpdatePosition(own.State);
    }

    private void OnMessage(ChatMessage m)
    {
        if (m.Kind == MessageKind.Private && m.Peer != null)
        {
            var tab = _vm.GetPrivateTab(m.Peer);
            tab.Add(m);
            if (!m.Outgoing)
            {
                if (_vm.SelectedTab != tab) tab.Unread = true;
                if (_settings.PlaySoundOnPrivateMessage) SystemSounds.Asterisk.Play();
            }
            return;
        }
        _vm.RadioTab.Add(m);
        if (_vm.SelectedTab != _vm.RadioTab && !m.Outgoing) _vm.RadioTab.Unread = true;
    }

    // ---- connection ------------------------------------------------------------------------

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (_session.IsConnected)
        {
            await _session.DisconnectAsync();
            return;
        }
        if (_settings.Cid == 0)
        {
            MessageBox.Show(this, "Сначала укажите CID и пароль в настройках.", "SkyPilot");
            OnSettingsClick(sender, e);
            if (_settings.Cid == 0) return;
        }
        var dialog = new ConnectWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _settings.Save(_settingsPath);

        var server = _settings.CurrentServer;
        ConnectButton.IsEnabled = false;
        try
        {
            _connectInfo = new ConnectInfo(server.Host, server.Port, _settings.Cid,
                _protector.Unprotect(_settings.ProtectedPassword), _settings.LastCallsign, _settings.LastTypeCode,
                _settings.RealName);
            await _session.ConnectAsync(_connectInfo);
        }
        catch (FsdLoginException ex)
        {
            Error("Не удалось подключиться: " + ex.Message);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    // ---- flight plan (filed on the website) ------------------------------------------------

    private WebsiteClient? Website() =>
        WebsiteClient.TryParseSite(_settings.Website, out var site) ? new WebsiteClient(Http, site) : null;

    private void OnFlightPlanClick(object sender, RoutedEventArgs e)
    {
        var site = Website();
        if (site == null)
        {
            Error("Укажите адрес сайта SkyNetwork в настройках.");
            return;
        }
        var callsign = _session.IsConnected ? _session.Callsign : _settings.LastCallsign;
        Process.Start(new ProcessStartInfo(site.FlightPlanPage(callsign).ToString()) { UseShellExecute = true });
        Info("План полёта подаётся на сайте. После подачи нажмите ОБНОВИТЬ.");
    }

    private async void OnRefreshFlightPlanClick(object sender, RoutedEventArgs e) => await RefreshFlightPlanAsync(quiet: false);

    /// <summary>
    /// Load the member's latest plan from the website; when connected, send it to the FSD server
    /// if it changed since the last time.
    /// </summary>
    private async Task RefreshFlightPlanAsync(bool quiet)
    {
        _nextPlanPoll = DateTime.UtcNow + FlightPlanPollInterval;
        var site = Website();
        if (site == null || _settings.Cid == 0)
        {
            if (!quiet) Error("Укажите CID и адрес сайта SkyNetwork в настройках.");
            return;
        }
        FlightPlan? plan;
        try
        {
            plan = await site.GetLatestFlightPlanAsync(_settings.Cid);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (!quiet) Error("Сайт SkyNetwork недоступен: " + ex.Message);
            return;
        }
        _vm.FlightPlan = plan;
        if (plan == null)
        {
            if (!quiet) Info("На сайте нет поданного плана полёта.");
            return;
        }
        if (_session.IsConnected && plan != _sentPlan)
        {
            try
            {
                await _session.SendFlightPlanAsync(plan);
                _sentPlan = plan;
            }
            catch (InvalidOperationException ex)
            {
                Error(ex.Message);
            }
        }
    }

    // ---- radios and transponder -------------------------------------------------------------

    private void OnModeCClick(object sender, RoutedEventArgs e) => _session.ModeC = _vm.ModeC;

    private void OnIdentClick(object sender, RoutedEventArgs e)
    {
        _session.Ident();
        _vm.Identing = true;
    }

    private void OnRxTxClick(object sender, RoutedEventArgs e)
    {
        _session.Com1Receive = _vm.Com1Rx;
        _session.Com2Receive = _vm.Com2Rx;
        _session.TransmitRadio = _vm.TxRadio;
        UpdateVoiceRadios();
        UpdateVoiceStatus();
    }

    private void OnComKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Escape)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            Keyboard.ClearFocus();
            return;
        }
        if (e.Key != Key.Enter) return;
        int radio = box.Tag as string == "2" ? 2 : 1;
        if (!Frequency.TryParse(box.Text, out var khz))
        {
            Error("Неверная частота. Пример: 118.100");
        }
        else if (!_sim.IsConnected)
        {
            Error("Симулятор не подключён");
        }
        else
        {
            _sim.SetComFrequency(radio, khz);
        }
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        Keyboard.ClearFocus();
    }

    private void OnSquawkKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        if (e.Key == Key.Enter)
        {
            if (!CommandProcessor.TryParseSquawk(SquawkBox.Text.Trim(), out var code)) Error("Код ответчика — 4 цифры от 0 до 7");
            else if (!_sim.IsConnected) Error("Симулятор не подключён");
            else _sim.SetTransponderCode(code);
        }
        SquawkBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        Keyboard.ClearFocus();
    }

    private void OnControllerDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: AtcRow row } && _sim.IsConnected)
            _sim.SetComFrequency(_vm.TxRadio, row.FrequencyKhz);
    }

    // ---- voice -----------------------------------------------------------------------------------

    private void UpdateVoiceRadios() =>
        _voice.UpdateRadios(_own?.Com1Khz ?? 0, _own?.Com2Khz ?? 0, _vm.Com1Rx, _vm.Com2Rx, _vm.TxRadio);

    private void UpdateVoiceStatus()
    {
        _vm.VoiceState = _voice.State;
        _vm.VoiceFailing = _voice.Failing;
        _vm.Transmitting = _voice.Transmitting;
        _vm.SetHeard(_voice.HeardOn(1), _voice.HeardOn(2));
    }

    private void OnVoiceClick(object sender, RoutedEventArgs e)
    {
        if (!_voice.Reconnect())
            Info("Голосовая связь включается автоматически при подключении к сети.");
    }

    private void OnPttDown(object sender, MouseButtonEventArgs e)
    {
        if (!_session.IsConnected) Error("Нет подключения к сети");
        else if (_voice.State != SkyNetwork.Voice.VoiceState.Connected) Error("Голос: нет связи с голосовым сервером");
        _voice.SetManualPtt(true);
    }

    private void OnPttUp(object sender, MouseEventArgs e) => _voice.SetManualPtt(false);

    // ---- settings -----------------------------------------------------------------------------

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        _settings.KeepWindowOnTop = _vm.Topmost;
        var dialog = new SettingsWindow(_settings, _protector, () => _voice.MicLevel) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _settings.Save(_settingsPath);
        _vm.Topmost = _settings.KeepWindowOnTop;
        _voice.ApplySettings(_settings);
    }

    // ---- chat input ---------------------------------------------------------------------------

    private async void OnSendClick(object sender, RoutedEventArgs e) => await SendInputAsync();

    private async void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Up && _history.Count > 0)
        {
            _historyIndex = Math.Max(0, _historyIndex - 1);
            Input.Text = _history[_historyIndex];
            Input.CaretIndex = Input.Text.Length;
            return;
        }
        if (e.Key == Key.Down && _history.Count > 0)
        {
            _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
            Input.Text = _historyIndex < _history.Count ? _history[_historyIndex] : "";
            Input.CaretIndex = Input.Text.Length;
            return;
        }
        if (e.Key == Key.Enter) await SendInputAsync();
    }

    private async Task SendInputAsync()
    {
        string line = Input.Text.Trim();
        if (line.Length == 0) return;
        Input.Clear();
        _history.Add(line);
        _historyIndex = _history.Count;

        // In a private tab, plain text goes to that pilot / controller.
        if (_vm.SelectedTab?.Peer is { } peer && !line.StartsWith('.'))
        {
            await Run(() => _session.SendPrivateAsync(peer, line));
            return;
        }
        await Run(async () =>
        {
            var feedback = await _commands.ExecuteAsync(line);
            if (feedback != null) Info(feedback);
        });
    }

    private async Task Run(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException ex)
        {
            Error(ex.Message);
        }
    }

    /// <summary>Keep chat lists scrolled to the newest line.</summary>
    private void OnChatLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBox list || list.ItemsSource is not INotifyCollectionChanged items) return;
        items.CollectionChanged += (_, _) =>
        {
            if (list.Items.Count > 0) list.ScrollIntoView(list.Items[^1]);
        };
    }
}
