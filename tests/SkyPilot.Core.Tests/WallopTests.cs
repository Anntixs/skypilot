using SkyPilot.Core.Matching;
using SkyPilot.Core.Session;

namespace SkyPilot.Core.Tests;

public class WallopTests
{
    [Fact]
    public async Task Wallop_GoesToSupervisors()
    {
        await using var server = new FakeFsdServer();
        var sim = new FakeSimulator();
        sim.Connect();
        var session = new NetworkSession(sim, new ModelMatcher());
        sim.Push(FakeSimulator.Own());
        var messages = new List<ChatMessage>();
        session.MessageReceived += (_, m) => { lock (messages) messages.Add(m); };
        await session.ConnectAsync(new ConnectInfo("127.0.0.1", server.Port, 1000001, "secret", "AFL123", "A20N", "Ivan Petrov"));

        var cmd = new CommandProcessor(session, sim);
        Assert.StartsWith("Пример", await cmd.ExecuteAsync(".wallop"));
        Assert.Equal("Запрос отправлен супервайзерам", await cmd.ExecuteAsync(".wallop AFL456 blocks the runway: need help"));
        // A colon would split the FSD fields: the text is sent without it.
        Assert.Equal("#TMAFL123:*S:AFL456 blocks the runway  need help", server.Expect("#TM"));
        lock (messages) Assert.Contains(messages, m => m.Outgoing && m.Text.Contains("need help"));
        await session.DisconnectAsync();
    }
}
