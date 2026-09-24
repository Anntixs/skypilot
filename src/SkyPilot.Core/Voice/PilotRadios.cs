using SkyNetwork.Voice;
using SkyPilot.Core.Model;
using SkyPilot.Core.Settings;

namespace SkyPilot.Core.Voice;

/// <summary>How the aircraft's COM1/COM2 and settings look to the voice client.</summary>
public static class PilotRadios
{
    /// <summary>
    /// Voice channel of a COM frequency in Hz, or 0 if the radio is not on an airband frequency.
    /// The voice server matches frequencies exactly, so the simulator's value is turned into the
    /// channel name: MSFS reports 8.33 kHz channels by their real frequency (118.010 as 118.008),
    /// and 118.005 is the same frequency as 118.000.
    /// </summary>
    public static uint ChannelHz(int khz)
    {
        if (khz is < 118000 or > 136990) return 0;
        int block = khz - khz % 25;
        int channel = (khz % 25) switch
        {
            <= 5 => block,
            <= 12 => block + 10,
            <= 20 => block + 15,
            _ => block + 25,
        };
        return (uint)channel * 1000;
    }

    /// <summary>COM1 and COM2: receive from the RX buttons, transmit on the radio with TX on.</summary>
    public static IReadOnlyList<Radio> Build(int com1Khz, int com2Khz, bool com1Rx, bool com2Rx, int txRadio) =>
    [
        new Radio(ChannelHz(com1Khz), com1Rx, txRadio == 1),
        new Radio(ChannelHz(com2Khz), com2Rx, txRadio == 2),
    ];

    /// <summary>The aircraft is the only antenna site.</summary>
    public static AntennaSite Site(AircraftState s) => new(s.Latitude, s.Longitude, s.AltitudeFeet);

    /// <summary>Voice settings from the stored ones; devices are stored by name and looked up in the current lists.</summary>
    public static VoiceSettings ToVoiceSettings(AppSettings s, IReadOnlyList<string> inputs, IReadOnlyList<string> outputs) => new()
    {
        InputDevice = DeviceIndex(inputs, s.InputDevice),
        OutputDevice = DeviceIndex(outputs, s.OutputDevice),
        MicGain = (float)Math.Clamp(s.MicGain, 0, 4),
        OutputVolume = (float)Math.Clamp(s.OutputVolume, 0, 2),
        Ptt = PttBinding.Parse(s.PttKey),
    };

    /// <summary>Index of a device by name; -1 (Windows default) if empty or no longer present.</summary>
    public static int DeviceIndex(IReadOnlyList<string> devices, string name)
    {
        if (name.Length == 0) return -1;
        for (int i = 0; i < devices.Count; i++)
            if (devices[i] == name) return i;
        return -1;
    }
}

/// <summary>Who is being heard right now and on which frequency (fed by the voice client's receive events).</summary>
public sealed class ReceiveTracker
{
    private readonly object _lock = new();
    private readonly List<(string Callsign, uint FrequencyHz)> _active = [];

    public void Update(string callsign, uint frequencyHz, bool active)
    {
        lock (_lock)
        {
            _active.RemoveAll(a => a.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase));
            if (active) _active.Add((callsign.ToUpperInvariant(), frequencyHz));
        }
    }

    public void Clear()
    {
        lock (_lock) _active.Clear();
    }

    /// <summary>Callsigns heard on a frequency, e.g. "AFL123" or "AFL123, SBI45"; empty if nobody.</summary>
    public string On(uint frequencyHz)
    {
        if (frequencyHz == 0) return "";
        lock (_lock) return string.Join(", ", _active.Where(a => a.FrequencyHz == frequencyHz).Select(a => a.Callsign));
    }
}
