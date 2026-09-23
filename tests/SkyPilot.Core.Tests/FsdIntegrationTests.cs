using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SkyPilot.Core.Matching;
using SkyPilot.Core.Session;

namespace SkyPilot.Core.Tests;

/// <summary>
/// End-to-end test against a real SkyNetwork FSD server (repository Skynetwork-fsd).
/// Runs only when SKYNET_FSD_BUILD points at its build directory (with skynet-fsd and skynet-admin).
/// </summary>
public class FsdIntegrationTests
{
    private static readonly string? Build = Environment.GetEnvironmentVariable("SKYNET_FSD_BUILD");

    [Fact]
    public async Task TwoPilotsSeeEachOther()
    {
        if (string.IsNullOrEmpty(Build)) return; // not configured
        var dir = Directory.CreateTempSubdirectory("skypilot");
        string db = Path.Combine(dir.FullName, "net.db");
        Run("skynet-admin", $"--db {db} adduser 1000001 \"Pilot One\" pw1");
        Run("skynet-admin", $"--db {db} adduser 1000002 \"Pilot Two\" pw2");
        int port = FreePort(), httpPort = FreePort();
        using var fsd = Process.Start(new ProcessStartInfo(Path.Combine(Build, "skynet-fsd"),
            $"--db {db} --host 127.0.0.1 --port {port} --http-port {httpPort}") { RedirectStandardError = true })!;
        try
        {
            await Task.Delay(300);
            var simA = new FakeSimulator();
            simA.Connect();
            var simB = new FakeSimulator();
            simB.Connect();
            var a = new NetworkSession(simA, new ModelMatcher());
            var b = new NetworkSession(simB, new ModelMatcher());
            simA.Push(FakeSimulator.Own(55.97, 37.41));
            simB.Push(FakeSimulator.Own(55.99, 37.42));
            var bMessages = new List<ChatMessage>();
            b.MessageReceived += (_, m) => { lock (bMessages) bMessages.Add(m); };

            await a.ConnectAsync(new ConnectInfo("127.0.0.1", port, 1000001, "pw1", "AFL123", "B748", "Pilot One"));
            await b.ConnectAsync(new ConnectInfo("127.0.0.1", port, 1000002, "pw2", "SBI456", "A20N", "Pilot Two"));
            await a.SendPositionAsync(); // don't wait for the 5 s timer

            // B draws A with the type A announced (B748) after the plane-info exchange.
            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (!simB.Aircraft.ContainsKey("AFL123") && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert.Equal("Boeing 747-8i Asobo", simB.Aircraft["AFL123"].Title);

            await a.SendRadioAsync("hello on 118.1");
            await a.SendPrivateAsync("SBI456", "private hello");
            deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                lock (bMessages) if (bMessages.Count(m => m.Kind is MessageKind.Radio or MessageKind.Private) >= 2) break;
                await Task.Delay(50);
            }
            lock (bMessages)
            {
                Assert.Contains(bMessages, m => m is { Kind: MessageKind.Radio, Text: "hello on 118.1", From: "AFL123" });
                Assert.Contains(bMessages, m => m is { Kind: MessageKind.Private, Text: "private hello", Peer: "AFL123" });
            }

            await a.DisconnectAsync();
            deadline = DateTime.UtcNow.AddSeconds(3);
            while (simB.Aircraft.ContainsKey("AFL123") && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert.False(simB.Aircraft.ContainsKey("AFL123"));
            await b.DisconnectAsync();
        }
        finally
        {
            fsd.Kill();
            dir.Delete(true);
        }
    }

    private static void Run(string tool, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(Build!, tool), args) { RedirectStandardOutput = true })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
