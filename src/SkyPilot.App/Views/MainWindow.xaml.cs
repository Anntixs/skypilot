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
using SkyPilot.Core.Simulation;
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
    private readonly XPlaneSimulator _xplane = new();
    private readonly SimulatorHub _sim;
    private readonly ModelMatcher _msfsMatcher;
    private ModelMatcher? _p3dMatcher;
    private bool _fsltlReported;
    private readonly NetworkSession _session;
    private readonly CommandProcessor _commands;
    private readonly PilotVoice _voice = new();
    private readonly DispatcherTimer _simRetry = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<string> _history = [];
    private int _historyIndex;
    private DateTime _nextPlanPoll;
    private FlightPlan? _sentPlan;
    /// <summary>The plan loaded from SimBrief: it is used until REFRESH is clicked or another plan is filed on the website.</summary>
    private FlightPlan? _simbriefPlan;
    /// <summary>The website's plan when SimBrief was loaded, so that a plan filed there later can be told apart.</summary>
    private FlightPlan? _websitePlanAtSimbrief;
    private bool _websiteKnownAtSimbrief;
    private ConnectInfo? _connectInfo;
    private OwnAircraftData? _own;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _settings = AppSettings.Load(_settingsPath);
        _vm.Topmost = _settings.KeepWindowOnTop;

        _sim = new SimulatorHub(
        [
            (SimulatorKind.XPlane, _xplane),
            (SimulatorKind.Msfs, new SimConnectSimulator(SimConnectFlavor.Msfs)),
            (SimulatorKind.Prepar3D, new SimConnectSimulator(SimConnectFlavor.Prepar3D(() => _settings.P3dSimConnectPath))),
        ])
        { Preferred = _settings.Simulator };
        var fsltl = FsltlLibrary.Load(CommunityFolders.Find(_settings.CommunityFolder));
        _msfsMatcher = ModelMatcher.Load(MatchingFile, fsltl);
        _session = new NetworkSession(_sim, _msfsMatcher);
        _commands = new CommandProcessor(_session, _sim);

        _sim.ConnectionChanged += (_, connected) => Ui(() => OnSimConnectionChanged(connected));
        _xplane.PluginLog += (_, text) => Ui(() => Info("X-Plane plugin: " + text));
        _sim.OwnAircraftUpdated += (_, own) => Ui(() => OnOwnAircraft(own));
        _session.ConnectionChanged += (_, connected) => Ui(() => OnNetworkConnectionChanged(connected));
        _session.MessageReceived += (_, m) => Ui(() => OnMessage(m));
        _session.ControllersChanged += (_, _) => Ui(UpdateControllers);
        _session.AtisReceived += (_, _) => Ui(UpdateControllers);
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
            "Welcome to SkyPilot! Start your simulator (MSFS, Prepar3D or X-Plane), then click OFFLINE to connect. Commands: .help", DateTime.UtcNow));
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

    private static string MatchingFile => Path.Combine(AppSettings.DefaultDirectory, "model-matching.json");

    private void TryConnectSim()
    {
        if (_sim.Active != null) return;
        _sim.Connect();
        // In automatic mode a missing MSFS library is no problem for someone flying Prepar3D: only the chosen simulator reports.
        if (_sim.Preferred != SimulatorKind.Auto && _sim.LastError != null && _vm.SimError == null) Error(_sim.LastError);
        _vm.SimError = _sim.LastError;
    }

    private async void OnSimConnectionChanged(bool connected)
    {
        _vm.SimConnected = connected;
        _vm.SimName = _sim.Name;
        if (!connected) return;
        Info($"Connected to {_sim.Name}.");
        switch (_sim.ActiveKind)
        {
            case SimulatorKind.Prepar3D:
                // The installed aircraft are read once: their titles are the models Prepar3D can show.
                if (_p3dMatcher == null)
                {
                    var library = await Task.Run(() => SimObjectsLibrary.Load(SimObjectsLibrary.Prepar3DFolders(SimConnectFlavor.Prepar3DFolders())));
                    _p3dMatcher = ModelMatcher.ForLibrary(library, ModelMatcher.LoadUserRules(MatchingFile));
                    Info(library.IsEmpty
                        ? "No Prepar3D aircraft with an ICAO type found: other aircraft are shown with your own model."
                        : $"Prepar3D aircraft: {library.Rules.Count} liveries found for other aircraft.");
                }
                _session.Matcher = _p3dMatcher;
                break;
            case SimulatorKind.XPlane:
                Info("X-Plane draws other aircraft with the CSL models installed for the SkyPilot plugin.");
                break;
            default:
                _session.Matcher = _msfsMatcher;
                if (!_fsltlReported)
                {
                    _fsltlReported = true;
                    var fsltl = _msfsMatcher.Fsltl;
                    Info(fsltl.IsInstalled
                        ? $"FSLTL traffic models: {fsltl.Titles.Count} liveries found ({fsltl.PackagePath})."
                        : "FSLTL package not found: other aircraft will be shown with default MSFS models. Install FS Live Traffic Liveries (fsltl-traffic-base) for correct models and liveries.");
                }
                break;
        }
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
            MessageBox.Show(this, "Enter your CID and password in Settings first.", "SkyPilot");
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
            Error("Could not connect: " + ex.Message);
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
            Error("Enter the SkyNetwork website address in Settings.");
            return;
        }
        var callsign = _session.IsConnected ? _session.Callsign : _settings.LastCallsign;
        Process.Start(new ProcessStartInfo(site.FlightPlanPage(callsign).ToString()) { UseShellExecute = true });
        Info("File your flight plan on the website, then click REFRESH.");
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
            if (!quiet) Error("Enter your CID and the SkyNetwork website address in Settings.");
            return;
        }
        FlightPlan? plan;
        try
        {
            plan = await site.GetLatestFlightPlanAsync(_settings.Cid);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (!quiet) Error("SkyNetwork website unavailable: " + ex.Message);
            return;
        }
        if (_simbriefPlan != null)
        {
            // The SimBrief plan stays until REFRESH is clicked or a different plan is filed on the website.
            bool newOnWebsite = _websiteKnownAtSimbrief && plan != _websitePlanAtSimbrief;
            if (quiet && !newOnWebsite)
            {
                await UsePlanAsync(_simbriefPlan);
                return;
            }
            _simbriefPlan = null;
        }
        if (plan == null)
        {
            _vm.FlightPlan = null;
            if (!quiet) Info("No flight plan filed on the website.");
            return;
        }
        await UsePlanAsync(plan);
    }

    /// <summary>Shows the plan and, when connected, sends it to the network if it changed since the last time.</summary>
    private async Task UsePlanAsync(FlightPlan plan)
    {
        _vm.FlightPlan = plan;
        if (!_session.IsConnected || plan == _sentPlan) return;
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

    /// <summary>
    /// SIMBRIEF: loads the pilot's latest SimBrief plan and files it on the network (at once when connected, otherwise
    /// when the connection is made); before connecting, the SimBrief callsign and aircraft also fill the connect window.
    /// </summary>
    private async void OnSimbriefClick(object sender, RoutedEventArgs e)
    {
        if (_settings.SimbriefUser.Trim().Length == 0)
        {
            Error("Enter your SimBrief username or Pilot ID in Settings, then click SIMBRIEF again.");
            return;
        }
        var (result, error) = await new SimbriefClient(Http).FetchAsync(_settings.SimbriefUser);
        if (result == null)
        {
            Error(error ?? "The SimBrief plan was not loaded.");
            return;
        }
        // What the website has now: from here on only a different plan filed there replaces the SimBrief one.
        _websiteKnownAtSimbrief = false;
        if (Website() is { } site && _settings.Cid != 0)
        {
            try
            {
                _websitePlanAtSimbrief = await site.GetLatestFlightPlanAsync(_settings.Cid);
                _websiteKnownAtSimbrief = true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                // The website is unavailable: the SimBrief plan simply stays until REFRESH.
            }
        }
        var plan = result.Plan;
        _simbriefPlan = plan;
        if (!_session.IsConnected && result.Callsign.Length > 0)
        {
            _settings.LastCallsign = result.Callsign;
            if (plan.AircraftType.Length > 0) _settings.LastTypeCode = plan.AircraftType;
        }
        await UsePlanAsync(plan);
        Info($"SimBrief plan loaded: {result.Callsign} {plan.Departure} → {plan.Destination}, {plan.AircraftType}, {plan.CruiseAltitude}" +
             (_session.IsConnected ? ". Filed on the network." : ". It is filed when you connect."));
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
            Error("Invalid frequency. Example: 118.100");
        }
        else if (!_sim.IsConnected)
        {
            Error("Simulator not connected");
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
            if (!CommandProcessor.TryParseSquawk(SquawkBox.Text.Trim(), out var code)) Error("Squawk code must be 4 digits from 0 to 7");
            else if (!_sim.IsConnected) Error("Simulator not connected");
            else _sim.SetTransponderCode(code);
        }
        SquawkBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        Keyboard.ClearFocus();
    }

    private void UpdateControllers() => _vm.SetControllers(_session.Controllers, _session.Atis);

    /// <summary>Double click: ATIS stations are requested, controllers are tuned on the TX radio.</summary>
    private async void OnControllerDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: AtcRow row }) return;
        if (row.IsAtis) await RequestAtisAsync(row);
        else TuneCom(_vm.TxRadio, row);
    }

    private async void OnRequestAtisClick(object sender, RoutedEventArgs e)
    {
        if (ControllerList.SelectedItem is AtcRow row) await RequestAtisAsync(row);
    }

    private void OnTuneCom1Click(object sender, RoutedEventArgs e)
    {
        if (ControllerList.SelectedItem is AtcRow row) TuneCom(1, row);
    }

    private void OnTuneCom2Click(object sender, RoutedEventArgs e)
    {
        if (ControllerList.SelectedItem is AtcRow row) TuneCom(2, row);
    }

    private async Task RequestAtisAsync(AtcRow row)
    {
        await Run(async () =>
        {
            await _session.RequestAtisAsync(row.Callsign);
            Info($"ATIS requested: {row.Callsign}");
        });
    }

    private void TuneCom(int radio, AtcRow row)
    {
        if (_sim.IsConnected) _sim.SetComFrequency(radio, row.FrequencyKhz);
        else Error("Simulator not connected");
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
            Info("Voice connects automatically when you connect to the network.");
    }

    private void OnPttDown(object sender, MouseButtonEventArgs e)
    {
        if (!_session.IsConnected) Error("Not connected to the network");
        else if (_voice.State != SkyNetwork.Voice.VoiceState.Connected) Error("Voice: no connection to the voice server");
        _voice.SetManualPtt(true);
    }

    private void OnPttUp(object sender, MouseEventArgs e) => _voice.SetManualPtt(false);

    // ---- settings -----------------------------------------------------------------------------

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        _settings.KeepWindowOnTop = _vm.Topmost;
        var dialog = new SettingsWindow(_settings, _protector, () => _voice.MicLevel) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (_sim.Preferred != _settings.Simulator)
        {
            // Another simulator chosen: let go of the current one; the retry timer attaches to the new choice.
            _sim.Preferred = _settings.Simulator;
            if (_sim.ActiveKind is { } kind && _settings.Simulator != SimulatorKind.Auto && kind != _settings.Simulator) _sim.Disconnect();
            _vm.SimError = null;
        }
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
