using SkyPilot.Core.Fsd;
using SkyPilot.Core.Matching;
using SkyPilot.Core.Model;
using SkyPilot.Core.Session;

namespace SkyPilot.Core.Tests;

public class SessionTests
{
    private static ConnectInfo Info(int port, string callsign = "AFL123") =>
        new("127.0.0.1", port, 1000001, "secret", callsign, "A20N", "Ivan Petrov");

    private static async Task<T> WaitFor<T>(Func<T?> probe, int timeoutMs = 3000) where T : class
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } v) return v;
            await Task.Delay(20);
        }
        throw new TimeoutException();
    }

    private static (FakeSimulator Sim, NetworkSession Session, List<ChatMessage> Messages) Create()
    {
        var sim = new FakeSimulator();
        sim.Connect();
        var session = new NetworkSession(sim, new ModelMatcher());
        sim.Push(FakeSimulator.Own());
        var messages = new List<ChatMessage>();
        session.MessageReceived += (_, m) => { lock (messages) messages.Add(m); };
        return (sim, session, messages);
    }

    [Fact]
    public async Task RequiresSimulator()
    {
        var session = new NetworkSession(new FakeSimulator(), new ModelMatcher());
        await Assert.ThrowsAsync<FsdLoginException>(() => session.ConnectAsync(Info(1)));
    }

    [Fact]
    public async Task LoginRejected_Throws()
    {
        await using var server = new FakeFsdServer("$ERserver:unknown:006:AFL123:Invalid CID/password");
        var (_, session, _) = Create();
        var ex = await Assert.ThrowsAsync<FsdLoginException>(() => session.ConnectAsync(Info(server.Port)));
        Assert.Equal("Invalid CID/password", ex.Message);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task Connect_SendsLoginAndPosition()
    {
        await using var server = new FakeFsdServer();
        var (_, session, messages) = Create();
        await session.ConnectAsync(Info(server.Port, "afl123"));

        Assert.Equal("#APAFL123:SERVER:1000001:secret:1:100:1:Ivan Petrov", server.Expect("#AP"));
        Assert.StartsWith("@S:AFL123:2000:1:55.970000:37.410000:3000:180:", server.Expect("@"));
        Assert.True(session.IsConnected);
        await WaitFor(() => { lock (messages) return messages.FirstOrDefault(m => m.Kind == MessageKind.Server); });

        session.ModeC = true;
        session.Ident();
        Assert.StartsWith("@Y:AFL123:", server.Expect("@"));

        await session.DisconnectAsync();
        Assert.Equal("#DPAFL123:1000001", server.Expect("#DP"));
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task Traffic_IsDrawnWithMatchedModel_AndRemoved()
    {
        await using var server = new FakeFsdServer();
        var (sim, session, _) = Create();
        await session.ConnectAsync(Info(server.Port));

        var state = new AircraftState(55.98, 37.40, 5000, 0, 0, 90, 250, false);
        await server.SendAsync(Packets.Position("SBI456", TransponderMode.ModeC, 1234, state, 5000));
        Assert.Equal("#SBAFL123:SBI456:PIR", server.Expect("#SB"));
        await server.SendAsync(Packets.PlaneInfoResponse("SBI456", "AFL123", "B748", "SBI"));

        var drawn = await WaitFor(() => sim.Aircraft.TryGetValue("SBI456", out var a) ? a.Title : null);
        Assert.Equal("Boeing 747-8i Asobo", drawn);
        Assert.Single(session.Traffic);

        // Model not installed: fall back to the default model.
        sim.FailCreate("SBI456", "Boeing 747-8i Asobo");
        Assert.Equal(ModelMatcher.FallbackTitle, sim.Aircraft["SBI456"].Title);

        await server.SendAsync("#DPSBI456:1000002");
        await WaitFor(() => sim.Aircraft.IsEmpty ? "" : null);
        Assert.Empty(session.Traffic);
        await session.DisconnectAsync();
    }

    [Fact]
    public async Task Messages_AreFilteredByFrequency()
    {
        await using var server = new FakeFsdServer();
        var (_, session, messages) = Create();
        await session.ConnectAsync(Info(server.Port));

        await server.SendAsync("#TMUUEE_TWR:@18100:AFL123 cleared to land");  // COM1
        await server.SendAsync("#TMUUEE_GND:@21500:on COM2");                   // COM2
        await server.SendAsync("#TMUUWW_TWR:@19700:not tuned");
        await server.SendAsync("#TMUUEE_TWR:AFL123:private: with colon");
        await server.SendAsync("#TMSUP1:*:network broadcast");

        await WaitFor(() => { lock (messages) return messages.Any(m => m.Kind == MessageKind.Broadcast) ? "" : null; });
        List<ChatMessage> got;
        lock (messages) got = messages.ToList();
        var radio = got.Where(m => m.Kind == MessageKind.Radio).ToList();
        Assert.Equal(["AFL123 cleared to land", "on COM2"], radio.Select(m => m.Text));
        var pm = Assert.Single(got, m => m.Kind == MessageKind.Private);
        Assert.Equal(("UUEE_TWR", "private: with colon"), (pm.Peer, pm.Text));

        await session.SendRadioAsync("wilco");
        Assert.Equal("#TMAFL123:@18100:wilco", server.Expect("#TM"));
        await session.SendPrivateAsync("uuee_twr", "thanks");
        Assert.Equal("#TMAFL123:UUEE_TWR:thanks", server.Expect("#TM"));
        await session.DisconnectAsync();
    }

    [Fact]
    public async Task AnswersQueries()
    {
        await using var server = new FakeFsdServer();
        var (_, session, _) = Create();
        await session.ConnectAsync(Info(server.Port));

        await server.SendAsync("#SBSBI456:AFL123:PIR");
        Assert.Equal("#SBAFL123:SBI456:PI:GEN:EQUIPMENT=A20N:AIRLINE=AFL", server.Expect("#SB"));
        await server.SendAsync("$PIUUEE_TWR:AFL123:12345");
        Assert.Equal("$POAFL123:UUEE_TWR:12345", server.Expect("$PO"));
        await server.SendAsync("$CQUUEE_TWR:AFL123:RN");
        Assert.Equal("$CRAFL123:UUEE_TWR:RN:Ivan Petrov::1", server.Expect("$CR"));
        await session.DisconnectAsync();
    }

    [Fact]
    public async Task Commands_ControlSimulator()
    {
        await using var server = new FakeFsdServer();
        var (sim, session, _) = Create();
        await session.ConnectAsync(Info(server.Port));
        var cmd = new CommandProcessor(session, sim);

        Assert.Equal("COM1: 118.700", await cmd.ExecuteAsync(".com1 118.7"));
        Assert.Equal((1, 118700), sim.ComChanges.Single());
        Assert.Equal("Ответчик: 7000", await cmd.ExecuteAsync(".x 7000"));
        Assert.Equal(7000, sim.Squawk);
        Assert.StartsWith("Код ответчика", await cmd.ExecuteAsync(".x 7800"));
        Assert.Null(await cmd.ExecuteAsync(".msg SBI456 hello there"));
        Assert.Equal("#TMAFL123:SBI456:hello there", server.Expect("#TM"));
        Assert.Null(await cmd.ExecuteAsync("request taxi"));
        Assert.Equal("#TMAFL123:@18100:request taxi", server.Expect("#TM"));
        Assert.StartsWith("Неизвестная команда", await cmd.ExecuteAsync(".foo"));
        await session.DisconnectAsync();
    }

    [Fact]
    public async Task ServerDrop_CleansUp()
    {
        await using var server = new FakeFsdServer();
        var (sim, session, messages) = Create();
        var states = new List<bool>();
        session.ConnectionChanged += (_, c) => { lock (states) states.Add(c); };
        await session.ConnectAsync(Info(server.Port));
        await server.SendAsync(Packets.Position("SBI456", TransponderMode.ModeC, 1234,
            new AircraftState(55.98, 37.40, 5000, 0, 0, 90, 250, false), 5000));
        await Task.Delay(100);

        server.DropClient();
        await WaitFor(() => { lock (states) return states.Contains(false) ? "" : null; });
        Assert.False(session.IsConnected);
        Assert.Empty(session.Traffic);
        Assert.Empty(sim.Aircraft);
    }
}
