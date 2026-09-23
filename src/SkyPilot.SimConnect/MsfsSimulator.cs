using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;
using SkyPilot.Core.Model;
using SkyPilot.Core.Simulation;

namespace SkyPilot.SimConnect;

/// <summary>
/// Microsoft Flight Simulator 2020 / 2024 via SimConnect.
/// A background thread pumps SimConnect messages; events are raised on that thread, but never
/// while the internal lock is held, so handlers may call back into this class.
/// </summary>
public sealed class MsfsSimulator : ISimulator
{
    private enum Definition { OwnAircraft = 1, RemoteAircraft = 2 }
    private enum Request { OwnAircraft = 1, AiRelease = 2, AiRemove = 3, CreateBase = 1000 }
    private enum ClientEvent { Com1SetHz = 1, Com2SetHz, TransponderSet, FreezeLatLon, FreezeAltitude, FreezeAttitude }
    private enum Group { Default = 1 }

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

    private sealed class SimAircraft(string callsign, string title)
    {
        public string Callsign { get; } = callsign;
        public string Title { get; } = title;
        public uint? ObjectId { get; set; }
        public uint RequestId { get; set; }
        public uint SendId { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, SimAircraft> _aircraft = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, SimAircraft> _byRequest = [];
    private readonly List<Action> _pendingEvents = [];
    private Microsoft.FlightSimulator.SimConnect.SimConnect? _sc;
    private AutoResetEvent? _signal;
    private Thread? _pump;
    private volatile bool _connected;
    private uint _nextRequest = (uint)Request.CreateBase;

    public string Name => "Microsoft Flight Simulator";
    public bool IsConnected => _connected;

    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<OwnAircraftData>? OwnAircraftUpdated;
    public event EventHandler<AircraftCreateFailedEventArgs>? AircraftCreateFailed;

    public bool Connect()
    {
        lock (_lock)
        {
            if (_sc != null) return true;
            var signal = new AutoResetEvent(false);
            try
            {
                _sc = new Microsoft.FlightSimulator.SimConnect.SimConnect("SkyPilot", IntPtr.Zero, 0, signal, 0);
            }
            catch (COMException)
            {
                signal.Dispose();
                return false; // simulator not running
            }
            _signal = signal;
            _sc.OnRecvOpen += OnOpen;
            _sc.OnRecvQuit += (_, _) => Defer(() => Disconnect());
            _sc.OnRecvException += OnException;
            _sc.OnRecvAssignedObjectId += OnAssignedObjectId;
            _sc.OnRecvSimobjectData += OnSimobjectData;
            _pump = new Thread(PumpLoop) { IsBackground = true, Name = "SimConnect" };
            _pump.Start();
            return true;
        }
    }

    public void Disconnect()
    {
        bool wasConnected;
        lock (_lock)
        {
            if (_sc == null) return;
            try { _sc.Dispose(); } catch (COMException) { }
            _sc = null;
            _aircraft.Clear();
            _byRequest.Clear();
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
            signal.WaitOne(250);
            lock (_lock)
            {
                if (_sc == null) return;
                try
                {
                    _sc.ReceiveMessage();
                }
                catch (COMException)
                {
                    Defer(() => Disconnect());
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

    private void OnOpen(Microsoft.FlightSimulator.SimConnect.SimConnect sc, SIMCONNECT_RECV_OPEN data)
    {
        void Add(Definition def, string name, string units, SIMCONNECT_DATATYPE type = SIMCONNECT_DATATYPE.FLOAT64) =>
            sc.AddToDataDefinition(def, name, units, type, 0, uint.MaxValue);

        Add(Definition.OwnAircraft, "PLANE LATITUDE", "degrees");
        Add(Definition.OwnAircraft, "PLANE LONGITUDE", "degrees");
        Add(Definition.OwnAircraft, "PLANE ALTITUDE", "feet");
        Add(Definition.OwnAircraft, "PLANE PITCH DEGREES", "degrees");
        Add(Definition.OwnAircraft, "PLANE BANK DEGREES", "degrees");
        Add(Definition.OwnAircraft, "PLANE HEADING DEGREES TRUE", "degrees");
        Add(Definition.OwnAircraft, "GROUND VELOCITY", "knots");
        Add(Definition.OwnAircraft, "SIM ON GROUND", "bool", SIMCONNECT_DATATYPE.INT32);
        Add(Definition.OwnAircraft, "PRESSURE ALTITUDE", "feet");
        Add(Definition.OwnAircraft, "COM ACTIVE FREQUENCY:1", "MHz");
        Add(Definition.OwnAircraft, "COM ACTIVE FREQUENCY:2", "MHz");
        Add(Definition.OwnAircraft, "TRANSPONDER CODE:1", "Bco16", SIMCONNECT_DATATYPE.INT32);
        sc.RegisterDataDefineStruct<OwnAircraftStruct>(Definition.OwnAircraft);

        Add(Definition.RemoteAircraft, "PLANE LATITUDE", "degrees");
        Add(Definition.RemoteAircraft, "PLANE LONGITUDE", "degrees");
        Add(Definition.RemoteAircraft, "PLANE ALTITUDE", "feet");
        Add(Definition.RemoteAircraft, "PLANE PITCH DEGREES", "degrees");
        Add(Definition.RemoteAircraft, "PLANE BANK DEGREES", "degrees");
        Add(Definition.RemoteAircraft, "PLANE HEADING DEGREES TRUE", "degrees");
        sc.RegisterDataDefineStruct<RemoteAircraftStruct>(Definition.RemoteAircraft);

        sc.MapClientEventToSimEvent(ClientEvent.Com1SetHz, "COM_RADIO_SET_HZ");
        sc.MapClientEventToSimEvent(ClientEvent.Com2SetHz, "COM2_RADIO_SET_HZ");
        sc.MapClientEventToSimEvent(ClientEvent.TransponderSet, "XPNDR_SET");
        sc.MapClientEventToSimEvent(ClientEvent.FreezeLatLon, "FREEZE_LATITUDE_LONGITUDE_SET");
        sc.MapClientEventToSimEvent(ClientEvent.FreezeAltitude, "FREEZE_ALTITUDE_SET");
        sc.MapClientEventToSimEvent(ClientEvent.FreezeAttitude, "FREEZE_ATTITUDE_SET");

        // About 10 updates per second of the user's aircraft.
        sc.RequestDataOnSimObject(Request.OwnAircraft, Definition.OwnAircraft,
            Microsoft.FlightSimulator.SimConnect.SimConnect.SIMCONNECT_OBJECT_ID_USER,
            SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 6, 0);

        _connected = true;
        Defer(() => ConnectionChanged?.Invoke(this, true));
    }

    private void OnSimobjectData(Microsoft.FlightSimulator.SimConnect.SimConnect sc, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if (data.dwRequestID != (uint)Request.OwnAircraft || data.dwData.Length == 0) return;
        var s = (OwnAircraftStruct)data.dwData[0];
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

    private void OnAssignedObjectId(Microsoft.FlightSimulator.SimConnect.SimConnect sc, SIMCONNECT_RECV_ASSIGNED_OBJECT_ID data)
    {
        if (!_byRequest.Remove(data.dwRequestID, out var ac)) return;
        if (!_aircraft.TryGetValue(ac.Callsign, out var current) || !ReferenceEquals(current, ac))
        {
            // Removed while it was being created.
            sc.AIRemoveObject(data.dwObjectID, Request.AiRemove);
            return;
        }
        ac.ObjectId = data.dwObjectID;
        sc.AIReleaseControl(data.dwObjectID, Request.AiRelease);
        foreach (var e in new[] { ClientEvent.FreezeLatLon, ClientEvent.FreezeAltitude, ClientEvent.FreezeAttitude })
            sc.TransmitClientEvent(data.dwObjectID, e, 1, Group.Default, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
    }

    private void OnException(Microsoft.FlightSimulator.SimConnect.SimConnect sc, SIMCONNECT_RECV_EXCEPTION data)
    {
        if (data.dwException != (uint)SIMCONNECT_EXCEPTION.CREATE_OBJECT_FAILED) return;
        var failed = _aircraft.Values.FirstOrDefault(a => a.ObjectId == null && a.SendId == data.dwSendID);
        if (failed == null) return;
        _aircraft.Remove(failed.Callsign);
        _byRequest.Remove(failed.RequestId);
        Defer(() => AircraftCreateFailed?.Invoke(this, new AircraftCreateFailedEventArgs(failed.Callsign, failed.Title)));
    }

    public void AddAircraft(string callsign, string modelTitle, AircraftState state)
    {
        lock (_lock)
        {
            if (_sc == null || !_connected) return;
            RemoveLocked(callsign);
            var ac = new SimAircraft(callsign, modelTitle) { RequestId = _nextRequest++ };
            var init = new SIMCONNECT_DATA_INITPOSITION
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
            _sc.AICreateNonATCAircraft(modelTitle, tail, init, (Request)ac.RequestId);
            _sc.GetLastSentPacketID(out var sendId);
            ac.SendId = sendId;
            _aircraft[callsign] = ac;
            _byRequest[ac.RequestId] = ac;
        }
    }

    public void UpdateAircraft(string callsign, AircraftState state)
    {
        lock (_lock)
        {
            if (_sc == null || !_aircraft.TryGetValue(callsign, out var ac) || ac.ObjectId is not { } id) return;
            var data = new RemoteAircraftStruct
            {
                Latitude = state.Latitude,
                Longitude = state.Longitude,
                Altitude = state.AltitudeFeet,
                Pitch = -state.PitchDegrees,
                Bank = -state.BankDegrees,
                Heading = state.HeadingDegrees,
            };
            _sc.SetDataOnSimObject(Definition.RemoteAircraft, id, SIMCONNECT_DATA_SET_FLAG.DEFAULT, data);
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
        if (ac.ObjectId is { } id && _sc != null) _sc.AIRemoveObject(id, Request.AiRemove);
    }

    public void SetComFrequency(int radio, int khz)
    {
        lock (_lock)
        {
            _sc?.TransmitClientEvent(Microsoft.FlightSimulator.SimConnect.SimConnect.SIMCONNECT_OBJECT_ID_USER,
                radio == 2 ? ClientEvent.Com2SetHz : ClientEvent.Com1SetHz, (uint)khz * 1000, Group.Default,
                SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        }
    }

    public void SetTransponderCode(int code)
    {
        lock (_lock)
        {
            _sc?.TransmitClientEvent(Microsoft.FlightSimulator.SimConnect.SimConnect.SIMCONNECT_OBJECT_ID_USER,
                ClientEvent.TransponderSet, ToBco16(code), Group.Default, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        }
    }

    /// <summary>Transponder codes are BCD: 7000 is 0x7000.</summary>
    internal static int FromBco16(int value) =>
        int.TryParse((value & 0xFFFF).ToString("X4"), out var code) ? code : 0;

    internal static uint ToBco16(int code) => Convert.ToUInt32(code.ToString("0000"), 16);

    public void Dispose()
    {
        Disconnect();
        _signal?.Dispose();
        _signal = null;
    }
}
