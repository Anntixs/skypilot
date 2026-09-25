using System.Net;
using System.Net.Sockets;
using System.Text;
using SkyPilot.Core.Model;
using SkyPilot.Core.Simulation;

namespace SkyPilot.Core.Tests;

public class XPlaneTests
{
    /// <summary>Stands in for the X-Plane plugin: a UDP socket that records what SkyPilot sends and answers.</summary>
    private sealed class FakePlugin : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        public IPEndPoint? App { get; private set; }

        public async Task<string> ReceiveAsync(string prefix)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var r = await _udp.ReceiveAsync(cts.Token);
                App = r.RemoteEndPoint;
                var text = Encoding.UTF8.GetString(r.Buffer);
                if (text.StartsWith(prefix, StringComparison.Ordinal)) return text;
            }
        }

        public void Send(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _udp.Send(bytes, bytes.Length, App!);
        }

        public void Dispose() => _udp.Dispose();
    }

    [Fact]
    public async Task Plugin_ConnectsSendsOwnAircraftAndTakesCommands()
    {
        using var plugin = new FakePlugin();
        using var sim = new XPlaneSimulator(plugin.Port);
        var own = new List<OwnAircraftData>();
        var logs = new List<string>();
        bool? connected = null;
        sim.ConnectionChanged += (_, c) => connected = c;
        sim.OwnAircraftUpdated += (_, o) => { lock (own) own.Add(o); };
        sim.PluginLog += (_, t) => { lock (logs) logs.Add(t); };

        Assert.False(sim.Connect());   // only asks the plugin
        Assert.Equal("HELLO 1", await plugin.ReceiveAsync("HELLO"));
        plugin.Send("HELLO 1 12060 1.0");
        // The event is raised just after IsConnected flips: wait for the event itself.
        await WaitUntil(() => connected == true);
        Assert.True(sim.Connect());
        Assert.Equal("X-Plane 12", sim.Name);
        Assert.True(sim.MatchesModels);

        plugin.Send("OWN 55.97 37.41 3000.5 2.5 -1 250 180 0 2990 118105 121500 7000 1");
        await WaitUntil(() => { lock (own) return own.Count == 1; });
        var o = own[0];
        Assert.Equal((55.97, 37.41, 3000.5, 250.0, 180.0, false), (o.State.Latitude, o.State.Longitude, o.State.AltitudeFeet, o.State.HeadingDegrees, o.State.GroundSpeedKnots, o.State.OnGround));
        Assert.Equal((2990.0, 118105, 121500, 7000, true), (o.PressureAltitudeFeet, o.Com1Khz, o.Com2Khz, o.TransponderCode, o.TransponderOn));

        plugin.Send("LOG No CSL models found");
        await WaitUntil(() => { lock (logs) return logs.Contains("No CSL models found"); });

        sim.AddAircraft("SBI456", new AircraftModel("", "A21N", "SBI"), new AircraftState(55.9, 37.4, 1200, 3, -2.5, 90, 150, false));
        Assert.Equal("ADD SBI456 A21N SBI -", await plugin.ReceiveAsync("ADD"));
        Assert.Equal("POS SBI456 55.9 37.4 1200 3 -2.5 90 150 0", await plugin.ReceiveAsync("POS"));
        sim.AddAircraft("N123", new AircraftModel("", "", ""), new AircraftState(1, 2, 3, 0, 0, 0, 0, true));
        Assert.Equal("ADD N123 - - -", await plugin.ReceiveAsync("ADD"));
        sim.SetComFrequency(2, 121500);
        Assert.Equal("COM 2 121500", await plugin.ReceiveAsync("COM"));
        sim.SetTransponderCode(421);
        Assert.Equal("XPDR 0421", await plugin.ReceiveAsync("XPDR"));
        sim.RemoveAircraft("SBI456");
        Assert.Equal("DEL SBI456", await plugin.ReceiveAsync("DEL"));
        sim.RemoveAllAircraft();
        Assert.Equal("CLEAR", await plugin.ReceiveAsync("CLEAR"));

        sim.Disconnect();
        Assert.Equal("BYE", await plugin.ReceiveAsync("BYE"));
        Assert.False(connected);
    }

    [Fact]
    public void Plugin_SilentForTooLong_Disconnects()
    {
        var now = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        using var sim = new XPlaneSimulator(1, () => now);
        bool? connected = null;
        sim.ConnectionChanged += (_, c) => connected = c;
        sim.OnMessage("HELLO 1 11550 1.0");
        Assert.True(sim.IsConnected);
        Assert.Equal("X-Plane 11", sim.Name);
        Assert.Null(XPlaneSimulator.ParseOwn(["OWN", "1"]));
        Assert.Null(XPlaneSimulator.ParseOwn("OWN a b c d e f g h i j k l m".Split(' ')));
        now += XPlaneSimulator.Timeout + TimeSpan.FromSeconds(1);
        sim.Connect();   // starts the one-second greeting, which notices the silence
        var until = DateTime.UtcNow.AddSeconds(3);
        while (connected != false && DateTime.UtcNow < until) Thread.Sleep(20);
        Assert.False(connected);
        Assert.False(sim.IsConnected);
    }

    [Fact]
    public void Hub_UsesTheSimulatorThatIsRunning()
    {
        var msfs = new FakeSimulator();
        var xp = new NotRunning();
        using var hub = new SimulatorHub([(SimulatorKind.XPlane, xp), (SimulatorKind.Msfs, msfs)]);
        var events = new List<bool>();
        hub.ConnectionChanged += (_, c) => events.Add(c);
        Assert.True(hub.Connect());
        Assert.Equal(SimulatorKind.Msfs, hub.ActiveKind);
        Assert.Equal([true], events);
        hub.AddAircraft("AFL1", new AircraftModel("Title", "A320", "AFL"), default);
        Assert.Equal("Title", msfs.Aircraft["AFL1"].Title);

        // Only X-Plane wanted: MSFS is left alone.
        hub.Disconnect();
        hub.Preferred = SimulatorKind.XPlane;
        Assert.False(hub.Connect());
        Assert.Equal("plugin not found", hub.LastError);
        Assert.Null(hub.ActiveKind);
    }

    private sealed class NotRunning : ISimulator
    {
        public string Name => "none";
        public bool IsConnected => false;
        public string? LastError => "plugin not found";
        public event EventHandler<bool>? ConnectionChanged { add { } remove { } }
        public event EventHandler<OwnAircraftData>? OwnAircraftUpdated { add { } remove { } }
        public event EventHandler<AircraftCreateFailedEventArgs>? AircraftCreateFailed { add { } remove { } }
        public bool Connect() => false;
        public void Disconnect() { }
        public void AddAircraft(string callsign, AircraftModel model, AircraftState state) { }
        public void UpdateAircraft(string callsign, AircraftState state) { }
        public void RemoveAircraft(string callsign) { }
        public void RemoveAllAircraft() { }
        public void SetComFrequency(int radio, int khz) { }
        public void SetTransponderCode(int code) { }
        public void Dispose() { }
    }

    private static async Task WaitUntil(Func<bool> probe)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!probe())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(20);
        }
    }
}
