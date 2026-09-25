using System.Collections.ObjectModel;
using SkyNetwork.Voice;
using SkyPilot.Core.Model;
using SkyPilot.Core.Session;

namespace SkyPilot.App.ViewModels;

/// <param name="Letter">Current ATIS letter of an ATIS station, once known; otherwise empty.</param>
/// <param name="AtisText">Last ATIS / controller information received from the station, or null (row tooltip).</param>
public sealed record AtcRow(string Callsign, string Frequency, string Facility, int FrequencyKhz,
    bool IsAtis = false, string Letter = "", string? AtisText = null)
{
    public bool HasLetter => Letter.Length > 0;
}

public sealed class MainViewModel : Observable
{
    private bool _simConnected;
    private string? _simError;
    private bool _netConnected;
    private string _callsign = "";
    private int _com1Khz, _com2Khz;
    private string _com1 = "---.---";
    private string _com2 = "---.---";
    private string _squawk = "----";
    private bool _modeC;
    private bool _identing;
    private bool _com1Rx = true, _com2Rx = true;
    private int _txRadio = 1;
    private int _trafficCount;
    private bool _atcVisible = true;
    private bool _topmost;
    private FlightPlan? _flightPlan;
    private string _utcTime = "";
    private ChatTab? _selectedTab;
    private List<AtcStation> _stations = [];
    private VoiceState _voiceState;
    private bool _voiceFailing;
    private bool _transmitting;
    private string _com1Heard = "", _com2Heard = "";

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

    // ---- connection -----------------------------------------------------------------

    public bool SimConnected { get => _simConnected; set { if (Set(ref _simConnected, value)) OnStatusChanged(); } }

    /// <summary>Why the simulator cannot be reached (e.g. SimConnect.dll missing), or null.</summary>
    public string? SimError { get => _simError; set { if (Set(ref _simError, value)) OnStatusChanged(); } }

    public bool NetConnected { get => _netConnected; set { if (Set(ref _netConnected, value)) OnStatusChanged(); } }
    public string Callsign { get => _callsign; set { if (Set(ref _callsign, value)) OnStatusChanged(); } }

    public string SimStatus => SimConnected ? "MSFS подключён"
        : SimError != null ? "Нет SimConnect.dll"
        : "Ожидание MSFS…";

    public string ConnectButtonText => NetConnected ? "ONLINE" : "OFFLINE";
    public string WindowTitle => NetConnected ? $"SkyPilot — {Callsign}" : "SkyPilot";

    private void OnStatusChanged()
    {
        RaisePropertyChanged(nameof(SimStatus));
        RaisePropertyChanged(nameof(ConnectButtonText));
        RaisePropertyChanged(nameof(WindowTitle));
    }

    // ---- radios -----------------------------------------------------------------------

    public string Com1 { get => _com1; set => Set(ref _com1, value); }
    public string Com2 { get => _com2; set => Set(ref _com2, value); }
    public string Squawk { get => _squawk; set => Set(ref _squawk, value); }
    public bool ModeC { get => _modeC; set { if (Set(ref _modeC, value)) RaisePropertyChanged(nameof(ModeCText)); } }
    public string ModeCText => ModeC ? "MODE C" : "STBY";
    public bool Identing { get => _identing; set => Set(ref _identing, value); }

    public bool Com1Rx { get => _com1Rx; set => Set(ref _com1Rx, value); }
    public bool Com2Rx { get => _com2Rx; set => Set(ref _com2Rx, value); }

    public int TxRadio
    {
        get => _txRadio;
        set
        {
            if (!Set(ref _txRadio, value)) return;
            RaisePropertyChanged(nameof(Com1Tx));
            RaisePropertyChanged(nameof(Com2Tx));
            RaisePropertyChanged(nameof(TxFrequency));
        }
    }

    public bool Com1Tx { get => TxRadio == 1; set { if (value) TxRadio = 1; else RaisePropertyChanged(nameof(Com1Tx)); } }
    public bool Com2Tx { get => TxRadio == 2; set { if (value) TxRadio = 2; else RaisePropertyChanged(nameof(Com2Tx)); } }

    /// <summary>The frequency text goes out on (shown above the chat).</summary>
    public string TxFrequency => TxRadio == 2 ? Com2 : Com1;

    /// <summary>ATC station tuned on each radio, or "-".</summary>
    public string Com1Station => StationOn(_com1Khz);
    public string Com2Station => StationOn(_com2Khz);

    private string StationOn(int khz) =>
        _stations.FirstOrDefault(s => Frequency.SameChannel(s.FrequencyKhz, khz))?.Callsign ?? "-";

    public void UpdateRadios(OwnAircraftData own)
    {
        _com1Khz = own.Com1Khz;
        _com2Khz = own.Com2Khz;
        Com1 = Frequency.Format(own.Com1Khz);
        Com2 = Frequency.Format(own.Com2Khz);
        Squawk = own.TransponderCode.ToString("0000");
        RaisePropertyChanged(nameof(TxFrequency));
        RaisePropertyChanged(nameof(Com1Station));
        RaisePropertyChanged(nameof(Com2Station));
    }

    public int TrafficCount { get => _trafficCount; set => Set(ref _trafficCount, value); }

    // ---- voice ---------------------------------------------------------------------------

    public VoiceState VoiceState
    {
        get => _voiceState;
        set { if (Set(ref _voiceState, value)) OnVoiceChanged(); }
    }

    public bool VoiceFailing { get => _voiceFailing; set { if (Set(ref _voiceFailing, value)) OnVoiceChanged(); } }
    public bool VoiceConnected => VoiceState == VoiceState.Connected;
    public string VoiceText => VoiceState == VoiceState.Connecting ? "ГОЛОС…" : "ГОЛОС";

    public string VoiceTip =>
        VoiceConnected ? "Голосовая связь: подключено. Щёлкните, чтобы переподключиться"
        : VoiceFailing ? "Голосовая связь: нет связи с голосовым сервером. Щёлкните, чтобы переподключиться"
        : VoiceState == VoiceState.Connecting ? "Голосовая связь: подключение…"
        : "Голосовая связь включается при подключении к сети";

    private void OnVoiceChanged()
    {
        RaisePropertyChanged(nameof(VoiceConnected));
        RaisePropertyChanged(nameof(VoiceText));
        RaisePropertyChanged(nameof(VoiceTip));
    }

    public bool Transmitting
    {
        get => _transmitting;
        set { if (Set(ref _transmitting, value)) RaisePropertyChanged(nameof(PttText)); }
    }

    public string PttText => Transmitting ? "ПЕРЕДАЧА" : "PTT";

    /// <summary>"RX AFL123" while someone is heard on the radio, otherwise empty.</summary>
    public string Com1Heard { get => _com1Heard; private set => Set(ref _com1Heard, value); }
    public string Com2Heard { get => _com2Heard; private set => Set(ref _com2Heard, value); }

    public void SetHeard(string com1, string com2)
    {
        Com1Heard = com1.Length > 0 ? "RX " + com1 : "";
        Com2Heard = com2.Length > 0 ? "RX " + com2 : "";
    }

    // ---- flight plan -------------------------------------------------------------------

    public FlightPlan? FlightPlan
    {
        get => _flightPlan;
        set
        {
            if (!Set(ref _flightPlan, value)) return;
            RaisePropertyChanged(nameof(HasFlightPlan));
            RaisePropertyChanged(nameof(FlightPlanText));
        }
    }

    public bool HasFlightPlan => FlightPlan != null;

    public string FlightPlanText => FlightPlan is { } p
        ? $"{p.Departure} → {p.Destination}   {p.AircraftType}   {p.CruiseAltitude}"
        : "Нет плана полёта";

    // ---- misc ----------------------------------------------------------------------------

    public bool AtcVisible { get => _atcVisible; set => Set(ref _atcVisible, value); }
    public bool Topmost { get => _topmost; set => Set(ref _topmost, value); }
    public string UtcTime { get => _utcTime; set => Set(ref _utcTime, value); }

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

    public void SetControllers(IEnumerable<AtcStation> stations, IReadOnlyDictionary<string, AtisInfo> atis)
    {
        _stations = stations.ToList();
        Controllers.Clear();
        foreach (var s in _stations)
        {
            atis.TryGetValue(s.Callsign, out var info);
            string letter = s.IsAtis && info?.Letter is { } l ? l.ToString() : "";
            string? text = info is { Lines.Count: > 0 }
                ? $"{info.Text}\n\nПолучено в {info.ReceivedAt.ToLocalTime():HH:mm}"
                : null;
            Controllers.Add(new AtcRow(s.Callsign, Frequency.Format(s.FrequencyKhz), s.FacilityText, s.FrequencyKhz,
                s.IsAtis, letter, text));
        }
        RaisePropertyChanged(nameof(Com1Station));
        RaisePropertyChanged(nameof(Com2Station));
    }
}
