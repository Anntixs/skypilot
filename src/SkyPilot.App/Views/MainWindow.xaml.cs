using System.Collections.Specialized;
using System.IO;
using System.Media;
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
using SkyPilot.SimConnect;

namespace SkyPilot.App.Views;

public partial class MainWindow : Window
{
    private readonly string _settingsPath = Path.Combine(AppSettings.DefaultDirectory, "settings.json");
    private readonly DpapiProtector _protector = new();
    private readonly AppSettings _settings;
    private readonly MainViewModel _vm = new();
    private readonly MsfsSimulator _sim = new();
    private readonly NetworkSession _session;
    private readonly CommandProcessor _commands;
    private readonly DispatcherTimer _simRetry = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly List<string> _history = [];
    private int _historyIndex;
    private FlightPlan? _lastPlan;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _settings = AppSettings.Load(_settingsPath);
        Topmost = _settings.KeepWindowOnTop;

        var matcher = ModelMatcher.Load(Path.Combine(AppSettings.DefaultDirectory, "model-matching.json"));
        _session = new NetworkSession(_sim, matcher);
        _commands = new CommandProcessor(_session, _sim);

        _sim.ConnectionChanged += (_, connected) => Ui(() => _vm.SimConnected = connected);
        _sim.OwnAircraftUpdated += (_, own) => Ui(() => _vm.UpdateRadios(own));
        _session.ConnectionChanged += (_, connected) => Ui(() =>
        {
            _vm.NetConnected = connected;
            _vm.Callsign = _session.Callsign;
        });
        _session.MessageReceived += (_, m) => Ui(() => OnMessage(m));
        _session.ControllersChanged += (_, _) => Ui(() => _vm.SetControllers(_session.Controllers));
        _session.TrafficChanged += (_, _) => Ui(() => _vm.TrafficCount = _session.Traffic.Count);

        _simRetry.Tick += (_, _) =>
        {
            TryConnectSim();
            _vm.Identing = _session.IsIdenting;
        };
        _simRetry.Start();
        TryConnectSim();

        _vm.RadioTab.Add(new ChatMessage(MessageKind.Info, "SkyPilot",
            "Добро пожаловать в SkyPilot! Запустите MSFS, затем нажмите «Подключиться». Команды: .help", DateTime.UtcNow));
        Closing += (_, _) =>
        {
            // Send the logoff packet before the process exits (the core never resumes on the UI thread).
            _session.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            _sim.Dispose();
        };
    }

    private void Ui(Action action) => Dispatcher.BeginInvoke(action);

    private void TryConnectSim()
    {
        if (_sim.IsConnected) return;
        _sim.Connect();
        if (_sim.LastError != null && _vm.SimError == null)
            _vm.RadioTab.Add(new ChatMessage(MessageKind.Error, "SkyPilot", _sim.LastError, DateTime.UtcNow));
        _vm.SimError = _sim.LastError;
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
            await _session.ConnectAsync(new ConnectInfo(server.Host, server.Port, _settings.Cid,
                _protector.Unprotect(_settings.ProtectedPassword), _settings.LastCallsign, _settings.LastTypeCode,
                _settings.RealName));
        }
        catch (FsdLoginException ex)
        {
            _vm.RadioTab.Add(new ChatMessage(MessageKind.Error, "SkyPilot", "Не удалось подключиться: " + ex.Message, DateTime.UtcNow));
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private async void OnFlightPlanClick(object sender, RoutedEventArgs e)
    {
        var dialog = new FlightPlanWindow(_lastPlan, _settings.LastTypeCode, _session.IsConnected) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Plan == null) return;
        _lastPlan = dialog.Plan;
        await Run(() => _session.SendFlightPlanAsync(dialog.Plan));
    }

    private void OnModeCClick(object sender, RoutedEventArgs e) => _session.ModeC = _vm.ModeC;

    private void OnIdentClick(object sender, RoutedEventArgs e)
    {
        _session.Ident();
        _vm.Identing = true;
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings, _protector) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _settings.Save(_settingsPath);
        Topmost = _settings.KeepWindowOnTop;
    }

    private void OnControllerDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListView { SelectedItem: AtcRow row } && _sim.IsConnected)
            _sim.SetComFrequency(1, row.FrequencyKhz);
    }

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
        if (e.Key != Key.Enter) return;
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
            if (feedback != null)
                _vm.RadioTab.Add(new ChatMessage(MessageKind.Info, "SkyPilot", feedback, DateTime.UtcNow));
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
            _vm.RadioTab.Add(new ChatMessage(MessageKind.Error, "SkyPilot", ex.Message, DateTime.UtcNow));
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
