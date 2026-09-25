using System.Runtime.InteropServices;
using SkyPilot.Core.Model;
using SkyPilot.Core.Simulation;

namespace SkyPilot.SimConnect;

/// <summary>
/// Microsoft Flight Simulator 2020 / 2024 or Prepar3D v4–v6 via the SimConnect C API (see <see cref="SimConnectFlavor"/>).
/// A background thread pumps SimConnect messages; events are raised on that thread, but never
/// while the internal lock is held, so handlers may call back into this class.
/// </summary>
public sealed class SimConnectSimulator(SimConnectFlavor flavor) : ISimulator
{
    private const uint DefOwnAircraft = 1;
    private const uint DefRemoteAircraft = 2;
    private const uint DefOwnTitle = 3;
    private const uint ReqOwnTitle = 4;
    private const uint ReqOwnAircraft = 1;
    private const uint ReqAiRelease = 2;
    private const uint ReqAiRemove = 3;
    private const uint ReqCreateBase = 1000;
    private const uint EvtCom1SetHz = 1, EvtCom2SetHz = 2, EvtTransponderSet = 3;
    private const uint EvtFreezeLatLon = 4, EvtFreezeAltitude = 5, EvtFreezeAttitude = 6;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct OwnAircraftStruct
    {
        public double Latitude;
        public double Longitude;
        public double Altitude;
        public double Pitch;
        public double Bank;
        public double Heading;
        public double GroundSpeed;
        public int OnGround;
        public double PressureAltitude;
        public double Com1Mhz;
        public double Com2Mhz;
        public int TransponderBco16;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct RemoteAircraftStruct
    {
        public double Latitude;
        public double Longitude;
        public double Altitude;
        public double Pitch;
        public double Bank;
        public double Heading;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    private struct TitleStruct
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Title;
    }

    private sealed class SimAircraft(string callsign, string title, string requested, AircraftState state)
    {
        public string Callsign { get; } = callsign;
        public string Title { get; } = title;
        /// <summary>The model SkyPilot asked for; <see cref="Title"/> differs when the user's own model stands in for it.</summary>
        public string Requested { get; } = requested;
        public AircraftState State { get; set; } = state;
        public uint? ObjectId { get; set; }
        public uint RequestId { get; set; }
        public uint SendId { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, SimAircraft> _aircraft = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, SimAircraft> _byRequest = [];
    private readonly List<Action> _pendingEvents = [];
    private SimConnectApi? _api;
    private IntPtr _handle;
    private string? _ownTitle;
    private AutoResetEvent? _signal;
    private Thread? _pump;
    private volatile bool _connected;
    private uint _nextRequest = ReqCreateBase;

    public string Name => flavor.Name;
    public bool IsConnected => _connected;

    /// <summary>Why the last <see cref="Connect"/> failed (no SimConnect.dll), for the status bar; null when the simulator is simply not running.</summary>
    public string? LastError { get; private set; }

    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<OwnAircraftData>? OwnAircraftUpdated;
    public event EventHandler<AircraftCreateFailedEventArgs>? AircraftCreateFailed;

    public bool Connect()
    {
        lock (_lock)
        {
            if (_handle != IntPtr.Zero) return true;
            _api ??= SimConnectApi.Load(flavor.Candidates(), out var loadError) is { } api ? api : ReportMissing(loadError);
            if (_api == null) return false;
            var signal = new AutoResetEvent(false);
            int hr = _api.Open(out _handle, "SkyPilot", signal.SafeWaitHandle.DangerousGetHandle());
            if (hr != 0 || _handle == IntPtr.Zero)
            {
                _handle = IntPtr.Zero;
                signal.Dispose();
                LastError = null; // simulator is simply not running
                return false;
            }
            LastError = null;
            _signal = signal;
            _pump = new Thread(PumpLoop) { IsBackground = true, Name = "SimConnect " + flavor.Name };
            _pump.Start();
            return true;
        }
    }

    private SimConnectApi? ReportMissing(string? loadError)
    {
        LastError = loadError ?? flavor.MissingDll;
        return null;
    }

    public void Disconnect()
    {
        bool wasConnected;
        lock (_lock)
        {
            if (_handle == IntPtr.Zero) return;
            _api!.Close(_handle);
            _handle = IntPtr.Zero;
            _aircraft.Clear();
            _byRequest.Clear();
            _ownTitle = null;
            wasConnected = _connected;
            _connected = false;
            _signal?.Set();
        }
        if (wasConnected) ConnectionChanged?.Invoke(this, false);
    }

    private void PumpLoop()
    {
        while (true)
        {
            var signal = _signal;
            if (signal == null) return;
            try { signal.WaitOne(250); } catch (ObjectDisposedException) { return; }
            lock (_lock)
            {
                if (_handle == IntPtr.Zero) return;
                while (_api!.GetNextDispatch(_handle, out var data))
                {
                    Dispatch(data);
                    if (_handle == IntPtr.Zero) break;
                }
            }
            FlushEvents();
        }
    }

    /// <summary>Queue an event to raise after the lock is released.</summary>
    private void Defer(Action action) => _pendingEvents.Add(action);

    private void FlushEvents()
    {
        List<Action> events;
        lock (_lock)
        {
            if (_pendingEvents.Count == 0) return;
            events = [.. _pendingEvents];
            _pendingEvents.Clear();
        }
        foreach (var e in events) e();
    }

    private void Dispatch(IntPtr data)
    {
        var header = Marshal.PtrToStructure<SimConnectApi.Recv>(data);
        switch (header.Id)
        {
            case SimConnectApi.RecvId.Open:
                OnOpen();
                break;
            case SimConnectApi.RecvId.Quit:
                Defer(Disconnect);
                break;
            case SimConnectApi.RecvId.SimObjectData:
                OnSimObjectData(data);
                break;
            case SimConnectApi.RecvId.AssignedObjectId:
                OnAssignedObjectId(Marshal.PtrToStructure<SimConnectApi.RecvAssignedObjectId>(data));
                break;
            case SimConnectApi.RecvId.Exception:
                OnException(Marshal.PtrToStructure<SimConnectApi.RecvException>(data));
                break;
        }
    }

    private void OnOpen()
    {
        var api = _api!;
        void Add(uint def, string name, string? units, SimConnectApi.DataType type = SimConnectApi.DataType.Float64) =>
            api.AddToDataDefinition(_handle, def, name, units, type);

        Add(DefOwnAircraft, "PLANE LATITUDE", "degrees");
        Add(DefOwnAircraft, "PLANE LONGITUDE", "degrees");
        Add(DefOwnAircraft, "PLANE ALTITUDE", "feet");
        Add(DefOwnAircraft, "PLANE PITCH DEGREES", "degrees");
        Add(DefOwnAircraft, "PLANE BANK DEGREES", "degrees");
        Add(DefOwnAircraft, "PLANE HEADING DEGREES TRUE", "degrees");
        Add(DefOwnAircraft, "GROUND VELOCITY", "knots");
        Add(DefOwnAircraft, "SIM ON GROUND", "bool", SimConnectApi.DataType.Int32);
        Add(DefOwnAircraft, "PRESSURE ALTITUDE", "feet");
        Add(DefOwnAircraft, "COM ACTIVE FREQUENCY:1", "MHz");
        Add(DefOwnAircraft, "COM ACTIVE FREQUENCY:2", "MHz");
        Add(DefOwnAircraft, "TRANSPONDER CODE:1", "Bco16", SimConnectApi.DataType.Int32);

        Add(DefRemoteAircraft, "PLANE LATITUDE", "degrees");
        Add(DefRemoteAircraft, "PLANE LONGITUDE", "degrees");
        Add(DefRemoteAircraft, "PLANE ALTITUDE", "feet");
        Add(DefRemoteAircraft, "PLANE PITCH DEGREES", "degrees");
        Add(DefRemoteAircraft, "PLANE BANK DEGREES", "degrees");
        Add(DefRemoteAircraft, "PLANE HEADING DEGREES TRUE", "degrees");

        // The user's aircraft model: it stands in for traffic models the simulator does not have.
        Add(DefOwnTitle, "TITLE", null, SimConnectApi.DataType.String256);

        api.MapClientEventToSimEvent(_handle, EvtCom1SetHz, flavor.ComHz ? "COM_RADIO_SET_HZ" : "COM_RADIO_SET");
        api.MapClientEventToSimEvent(_handle, EvtCom2SetHz, flavor.ComHz ? "COM2_RADIO_SET_HZ" : "COM2_RADIO_SET");
        api.MapClientEventToSimEvent(_handle, EvtTransponderSet, "XPNDR_SET");
        api.MapClientEventToSimEvent(_handle, EvtFreezeLatLon, "FREEZE_LATITUDE_LONGITUDE_SET");
        api.MapClientEventToSimEvent(_handle, EvtFreezeAltitude, "FREEZE_ALTITUDE_SET");
        api.MapClientEventToSimEvent(_handle, EvtFreezeAttitude, "FREEZE_ATTITUDE_SET");

        // About 10 updates per second of the user's aircraft, its model once a second.
        api.RequestDataOnSimObject(_handle, ReqOwnAircraft, DefOwnAircraft, SimConnectApi.ObjectIdUser, SimConnectApi.Period.SimFrame, 6);
        api.RequestDataOnSimObject(_handle, ReqOwnTitle, DefOwnTitle, SimConnectApi.ObjectIdUser, SimConnectApi.Period.Second, 0);

        _connected = true;
        Defer(() => ConnectionChanged?.Invoke(this, true));
    }

    private void OnSimObjectData(IntPtr data)
    {
        var header = Marshal.PtrToStructure<SimConnectApi.RecvSimObjectData>(data);
        if (header.RequestId == ReqOwnTitle)
        {
            var title = Marshal.PtrToStructure<TitleStruct>(data + SimConnectApi.SimObjectDataOffset).Title?.Trim();
            if (!string.IsNullOrEmpty(title)) _ownTitle = title;
            return;
        }
        if (header.RequestId != ReqOwnAircraft) return;
        var s = Marshal.PtrToStructure<OwnAircraftStruct>(data + SimConnectApi.SimObjectDataOffset);
        var own = new OwnAircraftData(
            new AircraftState(s.Latitude, s.Longitude, s.Altitude,
                PitchDegrees: -s.Pitch, BankDegrees: -s.Bank, s.Heading, s.GroundSpeed, s.OnGround != 0),
            s.PressureAltitude,
            (int)Math.Round(s.Com1Mhz * 1000),
            (int)Math.Round(s.Com2Mhz * 1000),
            FromBco16(s.TransponderBco16),
            TransponderOn: true);
        Defer(() => OwnAircraftUpdated?.Invoke(this, own));
    }

    private void OnAssignedObjectId(SimConnectApi.RecvAssignedObjectId data)
    {
        if (!_byRequest.Remove(data.RequestId, out var ac)) return;
        if (!_aircraft.TryGetValue(ac.Callsign, out var current) || !ReferenceEquals(current, ac))
        {
            // Removed while it was being created.
            _api!.AIRemoveObject(_handle, data.ObjectId, ReqAiRemove);
            return;
        }
        ac.ObjectId = data.ObjectId;
        _api!.AIReleaseControl(_handle, data.ObjectId, ReqAiRelease);
        foreach (var e in new[] { EvtFreezeLatLon, EvtFreezeAltitude, EvtFreezeAttitude })
            _api.TransmitClientEvent(_handle, data.ObjectId, e, 1);
    }

    private void OnException(SimConnectApi.RecvException data)
    {
        if (data.Exception != SimConnectApi.ExceptionCreateObjectFailed) return;
        var failed = _aircraft.Values.FirstOrDefault(a => a.ObjectId == null && a.SendId == data.SendId);
        if (failed == null) return;
        _aircraft.Remove(failed.Callsign);
        _byRequest.Remove(failed.RequestId);
        // The model is not installed: the user's own aircraft is always there, so draw that before giving up.
        if (_ownTitle is { } own && !string.Equals(failed.Title, own, StringComparison.OrdinalIgnoreCase))
        {
            CreateLocked(failed.Callsign, own, failed.Requested, failed.State);
            return;
        }
        Defer(() => AircraftCreateFailed?.Invoke(this, new AircraftCreateFailedEventArgs(failed.Callsign, failed.Requested)));
    }

    public void AddAircraft(string callsign, AircraftModel model, AircraftState state)
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero || !_connected) return;
            RemoveLocked(callsign);
            CreateLocked(callsign, model.Title, model.Title, state);
        }
    }

    private void CreateLocked(string callsign, string title, string requested, AircraftState state)
    {
        var ac = new SimAircraft(callsign, title, requested, state) { RequestId = _nextRequest++ };
        var init = new SimConnectApi.InitPosition
        {
            Latitude = state.Latitude,
            Longitude = state.Longitude,
            Altitude = state.AltitudeFeet,
            Pitch = -state.PitchDegrees,
            Bank = -state.BankDegrees,
            Heading = state.HeadingDegrees,
            OnGround = state.OnGround ? 1u : 0u,
            Airspeed = (uint)Math.Max(0, state.GroundSpeedKnots),
        };
        string tail = callsign.Length > 12 ? callsign[..12] : callsign;
        _api!.AICreateNonATCAircraft(_handle, title, tail, init, ac.RequestId);
        ac.SendId = _api.GetLastSentPacketId(_handle);
        _aircraft[callsign] = ac;
        _byRequest[ac.RequestId] = ac;
    }

    public void UpdateAircraft(string callsign, AircraftState state)
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero || !_aircraft.TryGetValue(callsign, out var ac)) return;
            ac.State = state;
            if (ac.ObjectId is not { } id) return;
            var data = new RemoteAircraftStruct
            {
                Latitude = state.Latitude,
                Longitude = state.Longitude,
                Altitude = state.AltitudeFeet,
                Pitch = -state.PitchDegrees,
                Bank = -state.BankDegrees,
                Heading = state.HeadingDegrees,
            };
            int size = Marshal.SizeOf<RemoteAircraftStruct>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(data, buffer, false);
                _api!.SetDataOnSimObject(_handle, DefRemoteAircraft, id, (uint)size, buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    public void RemoveAircraft(string callsign)
    {
        lock (_lock) RemoveLocked(callsign);
    }

    public void RemoveAllAircraft()
    {
        lock (_lock)
            foreach (var callsign in _aircraft.Keys.ToList())
                RemoveLocked(callsign);
    }

    private void RemoveLocked(string callsign)
    {
        if (!_aircraft.Remove(callsign, out var ac)) return;
        // Pending creations are cleaned up in OnAssignedObjectId.
        if (ac.ObjectId is { } id && _handle != IntPtr.Zero) _api!.AIRemoveObject(_handle, id, ReqAiRemove);
    }

    public void SetComFrequency(int radio, int khz)
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero) return;
            _api!.TransmitClientEvent(_handle, SimConnectApi.ObjectIdUser, radio == 2 ? EvtCom2SetHz : EvtCom1SetHz,
                flavor.ComHz ? (uint)khz * 1000 : ToComBcd16(khz));
        }
    }

    public void SetTransponderCode(int code)
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero) return;
            _api!.TransmitClientEvent(_handle, SimConnectApi.ObjectIdUser, EvtTransponderSet, ToBco16(code));
        }
    }

    /// <summary>Transponder codes are BCD: 7000 is 0x7000.</summary>
    internal static int FromBco16(int value) =>
        int.TryParse((value & 0xFFFF).ToString("X4"), out var code) ? code : 0;

    internal static uint ToBco16(int code) => Convert.ToUInt32(code.ToString("0000"), 16);

    /// <summary>
    /// COM_RADIO_SET takes the frequency as BCD without the leading 1 and the last digit: 118.105 is 0x1810.
    /// The simulator fills in the 25 kHz step (118.125 from 18.12), so 8.33 kHz channels land on the nearest one.
    /// </summary>
    internal static uint ToComBcd16(int khz) => Convert.ToUInt32(((khz - 100000) / 10).ToString("0000"), 16);

    public void Dispose()
    {
        Disconnect();
        _signal?.Dispose();
        _signal = null;
    }
}
