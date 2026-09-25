using SkyPilot.Core.Fsd;
using SkyPilot.Core.Matching;
using SkyPilot.Core.Session;

namespace SkyPilot.Core.Tests;

public class AtisTests
{
    private static ConnectInfo Info(int port) =>
        new("127.0.0.1", port, 1000001, "secret", "AFL123", "A20N", "Ivan Petrov");

    private static async Task WaitUntil(Func<bool> probe, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!probe())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    private sealed class Clock
    {
        public DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
    }

    private static (NetworkSession Session, List<ChatMessage> Messages, List<AtisInfo> Atis, Clock Clock) Create()
    {
        var sim = new FakeSimulator();
        sim.Connect();
        var clock = new Clock();
        var session = new NetworkSession(sim, new ModelMatcher(), () => clock.Now);
        sim.Push(FakeSimulator.Own());
        var messages = new List<ChatMessage>();
        var atis = new List<AtisInfo>();
        session.MessageReceived += (_, m) => { lock (messages) messages.Add(m); };
        session.AtisReceived += (_, a) => { lock (atis) atis.Add(a); };
        return (session, messages, atis, clock);
    }

    [Fact]
    public void RequestPacket_Format()
    {
        Assert.Equal("$CQAFL123:UUEE_ATIS:ATIS", Packets.AtisRequest("AFL123", "UUEE_ATIS"));
    }

    [Fact]
    public async Task Request_SendsClientQuery()
    {
        await using var server = new FakeFsdServer();
        var (session, _, _, _) = Create();
        await session.ConnectAsync(Info(server.Port));
        await session.RequestAtisAsync("uuee_atis");
        Assert.Equal("$CQAFL123:UUEE_ATIS:ATIS", server.Expect("$CQ"));

        var cmd = new CommandProcessor(session, new FakeSimulator());
        Assert.Equal("ATIS requested: UUDD_TWR", await cmd.ExecuteAsync(".atis uudd_twr"));
        Assert.Equal("$CQAFL123:UUDD_TWR:ATIS", server.Expect("$CQ"));
        await session.DisconnectAsync();
    }

    [Fact]
    public async Task TextLinesAndEnd_AreAssembled()
    {
        await using var server = new FakeFsdServer();
        var (session, messages, atis, _) = Create();
        await session.ConnectAsync(Info(server.Port));
        await session.RequestAtisAsync("UUEE_ATIS");

        await server.SendAsync("$CRUUEE_ATIS:AFL123:ATIS:V:voice.example.org/uuee");
        await server.SendAsync("$CRUUEE_ATIS:AFL123:ATIS:T:SHEREMETYEVO INFORMATION B TIME 1200");
        await server.SendAsync("$CRUUEE_ATIS:AFL123:ATIS:Z:1200z");
        await server.SendAsync("$CRUUEE_ATIS:AFL123:ATIS:T:RWY 24C: ILS APPROACH");
        await server.SendAsync("$CRUUEE_ATIS:AFL123:ATIS:E:2");

        await WaitUntil(() => { lock (atis) return atis.Count > 0; });
        var a = Assert.Single(atis);
        Assert.Equal("UUEE_ATIS", a.Station);
        Assert.Equal(["SHEREMETYEVO INFORMATION B TIME 1200", "RWY 24C: ILS APPROACH"], a.Lines);
        Assert.Equal('B', a.Letter);
        Assert.Same(a, session.Atis["uuee_atis"]);

        ChatMessage m;
        lock (messages) m = Assert.Single(messages, x => x.Kind == MessageKind.Atis);
        Assert.Equal("UUEE_ATIS", m.From);
        Assert.Equal("SHEREMETYEVO INFORMATION B TIME 1200\nRWY 24C: ILS APPROACH", m.Text);
        await session.DisconnectAsync();
    }

    [Fact]
    public async Task MissingEnd_CompletesAfterWait()
    {
        await using var server = new FakeFsdServer();
        var (session, messages, atis, clock) = Create();
        await session.ConnectAsync(Info(server.Port));
        await session.RequestAtisAsync("UUDD_ATIS");

        await server.SendAsync("$CRUUDD_ATIS:AFL123:ATIS:T:DOMODEDOVO ИНФОРМАЦИЯ К");
        await server.SendAsync("#TMUUDD_TWR:AFL123:done");
        await WaitUntil(() => { lock (messages) return messages.Any(m => m.Kind == MessageKind.Private); });
        session.RenderTick();
        lock (atis) Assert.Empty(atis);

        clock.Now += NetworkSession.AtisLineWait;
        session.RenderTick();
        await WaitUntil(() => { lock (atis) return atis.Count > 0; });
        AtisInfo a;
        lock (atis) a = Assert.Single(atis);
        Assert.Equal('K', a.Letter);
        await session.DisconnectAsync();
    }

    [Fact]
    public async Task NoReply_ReportsTimeout()
    {
        await using var server = new FakeFsdServer();
        var (session, messages, atis, clock) = Create();
        await session.ConnectAsync(Info(server.Port));
        await session.RequestAtisAsync("UUWW_ATIS");

        session.RenderTick();
        lock (messages) Assert.DoesNotContain(messages, m => m.Text.Contains("UUWW_ATIS"));
        clock.Now += NetworkSession.AtisReplyTimeout;
        session.RenderTick();
        await WaitUntil(() => { lock (messages) return messages.Any(m => m.Kind == MessageKind.Info && m.Text.Contains("UUWW_ATIS")); });
        lock (atis) Assert.Empty(atis);
        await session.DisconnectAsync();
    }

    [Fact]
    public async Task RepliesToOthers_AreIgnored()
    {
        await using var server = new FakeFsdServer();
        var (session, messages, atis, _) = Create();
        await session.ConnectAsync(Info(server.Port));

        await server.SendAsync("$CRUUEE_ATIS:SBI456:ATIS:T:INFORMATION C");
        await server.SendAsync("$CRUUEE_ATIS:SBI456:ATIS:E:1");
        // A marker addressed to us proves the earlier packets were processed.
        await server.SendAsync("#TMUUEE_TWR:AFL123:done");
        await WaitUntil(() => { lock (messages) return messages.Any(m => m.Kind == MessageKind.Private); });

        lock (atis) Assert.Empty(atis);
        Assert.Empty(session.Atis);
        lock (messages) Assert.DoesNotContain(messages, m => m.Kind == MessageKind.Atis);
        await session.DisconnectAsync();
    }

    [Theory]
    [InlineData("SHEREMETYEVO INFORMATION B TIME 1200", 'B')]
    [InlineData("ПУЛКОВО ИНФОРМАЦИЯ B", 'B')]
    [InlineData("ПУЛКОВО ИНФОРМАЦИЯ В", 'B')] // Cyrillic В
    [InlineData("UUEE ATIS INFORMATION BRAVO", 'B')]
    [InlineData("vnukovo information delta", 'D')]
    [InlineData("UUWW ATIS K 1230Z", 'K')]
    [InlineData("RWY 24C IN USE", null)]
    [InlineData("INFORMATION ON REQUEST", null)]
    public void Letter_IsExtracted(string text, char? expected)
    {
        Assert.Equal(expected, AtisInfo.ExtractLetter([text]));
    }

    [Theory]
    [InlineData("UUEE_ATIS", 0, "ATIS")]
    [InlineData("uuee_atis", 4, "ATIS")]
    [InlineData("UUEE_TWR", 4, "TWR")]
    [InlineData("UUEE_OBS", 0, "OBS")]
    [InlineData("ATISFAN", 0, "OBS")]
    public void AtisStations_AreClassified(string callsign, int facility, string expected)
    {
        var station = new AtcStation(callsign, 128025, facility, DateTime.UtcNow);
        Assert.Equal(expected, station.FacilityText);
        Assert.Equal(expected, AtcStation.FacilityName(callsign, facility));
        Assert.Equal(expected == "ATIS", station.IsAtis);
    }

    [Fact]
    public async Task AtisStation_AppearsInControllers()
    {
        await using var server = new FakeFsdServer();
        var (session, _, _, _) = Create();
        await session.ConnectAsync(Info(server.Port));
        await server.SendAsync("%UUEE_ATIS:28025:0:50:1:55.97:37.41:0");
        await WaitUntil(() => session.Controllers.Count > 0);
        var s = Assert.Single(session.Controllers);
        Assert.Equal(("ATIS", 128025), (s.FacilityText, s.FrequencyKhz));
        await session.DisconnectAsync();
    }
}
