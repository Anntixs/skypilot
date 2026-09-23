using System.Collections.ObjectModel;
using SkyPilot.Core.Model;
using SkyPilot.Core.Session;

namespace SkyPilot.App.ViewModels;

public sealed record AtcRow(string Callsign, string Frequency, string Facility, int FrequencyKhz);

public sealed class MainViewModel : Observable
{
    private bool _simConnected;
    private string? _simError;
    private bool _netConnected;
    private string _callsign = "";
    private string _com1 = "---.---";
    private string _com2 = "---.---";
    private string _squawk = "----";
    private bool _modeC;
    private bool _identing;
    private int _trafficCount;
    private ChatTab? _selectedTab;

    public MainViewModel()
    {
        RadioTab = new ChatTab("Радио", null);
        Tabs.Add(RadioTab);
        _selectedTab = RadioTab;
    }

    public ChatTab RadioTab { get; }
    public ObservableCollection<ChatTab> Tabs { get; } = [];
    public ObservableCollection<AtcRow> Controllers { get; } = [];

    public ChatTab? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (Set(ref _selectedTab, value) && value != null) value.Unread = false;
        }
    }

    public bool SimConnected
    {
        get => _simConnected;
        set
        {
            if (Set(ref _simConnected, value)) OnStatusChanged();
        }
    }

    public bool NetConnected
    {
        get => _netConnected;
        set
        {
            if (Set(ref _netConnected, value)) OnStatusChanged();
        }
    }

    public string Callsign
    {
        get => _callsign;
        set
        {
            if (Set(ref _callsign, value)) OnStatusChanged();
        }
    }

    /// <summary>Why the simulator cannot be reached (e.g. SimConnect.dll missing), or null.</summary>
    public string? SimError
    {
        get => _simError;
        set
        {
            if (Set(ref _simError, value)) OnStatusChanged();
        }
    }

    public string SimStatus => SimConnected ? "MSFS: подключён"
        : SimError != null ? "MSFS: нет SimConnect.dll"
        : "MSFS: ожидание симулятора…";
    public string NetStatus => NetConnected ? $"В сети: {Callsign}" : "Не в сети";
    public string ConnectButtonText => NetConnected ? "Отключиться" : "Подключиться";

    private void OnStatusChanged()
    {
        Raise(nameof(SimStatus));
        Raise(nameof(NetStatus));
        Raise(nameof(ConnectButtonText));
    }

    public string Com1 { get => _com1; set => Set(ref _com1, value); }
    public string Com2 { get => _com2; set => Set(ref _com2, value); }
    public string Squawk { get => _squawk; set => Set(ref _squawk, value); }
    public bool ModeC { get => _modeC; set => Set(ref _modeC, value); }
    public bool Identing { get => _identing; set => Set(ref _identing, value); }
    public int TrafficCount { get => _trafficCount; set => Set(ref _trafficCount, value); }

    public void UpdateRadios(OwnAircraftData own)
    {
        Com1 = Frequency.Format(own.Com1Khz);
        Com2 = Frequency.Format(own.Com2Khz);
        Squawk = own.TransponderCode.ToString("0000");
    }

    public ChatTab GetPrivateTab(string peer)
    {
        var tab = Tabs.FirstOrDefault(t => t.Peer == peer);
        if (tab == null)
        {
            tab = new ChatTab(peer, peer);
            Tabs.Add(tab);
        }
        return tab;
    }

    public void SetControllers(IEnumerable<AtcStation> stations)
    {
        Controllers.Clear();
        foreach (var s in stations)
            Controllers.Add(new AtcRow(s.Callsign, Frequency.Format(s.FrequencyKhz), AtcStation.FacilityName(s.Facility), s.FrequencyKhz));
    }

    private void Raise(string name) => RaisePropertyChanged(name);
}
