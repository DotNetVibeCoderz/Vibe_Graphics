using System.Numerics;
using System.Text;
using ThreeNet.Networking;
using Xunit;

namespace ThreeNet.Tests;

public class NetworkingTests
{
    private static bool Pump(Func<bool> done, params NetworkSession[] sessions)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            foreach (NetworkSession session in sessions)
            {
                session.Update();
            }

            if (done())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return false;
    }

    [Fact]
    public void ClientsConnectAndExchangeReliableMessages()
    {
        using NetworkSession host = NetworkSession.Host(0, "host");
        using NetworkSession alice = NetworkSession.Connect("127.0.0.1", host.Port, "alice");
        using NetworkSession bob = NetworkSession.Connect("127.0.0.1", host.Port, "bob");

        List<string> hostLog = [], aliceLog = [], bobLog = [];
        host.MessageReceived += m => hostLog.Add($"{m.Sender.Name}:{Encoding.UTF8.GetString(m.Data)}");
        alice.MessageReceived += m => aliceLog.Add($"{m.Sender.Name}:{Encoding.UTF8.GetString(m.Data)}");
        bob.MessageReceived += m => bobLog.Add($"{m.Sender.Name}:{Encoding.UTF8.GetString(m.Data)}");

        Assert.True(Pump(() => alice.IsConnected && bob.IsConnected && host.Peers.Count == 2
            && alice.Peers.Count == 2 && bob.Peers.Count == 2, host, alice, bob), "everyone should know everyone");
        Assert.NotEqual(alice.LocalPeerId, bob.LocalPeerId);
        Assert.Contains(alice.Peers, p => p.Name == "bob");

        // Ordered reliable delivery, relayed through the host.
        for (int i = 0; i < 20; i++)
        {
            alice.Send(Encoding.UTF8.GetBytes($"m{i}"));
        }

        host.Send(Encoding.UTF8.GetBytes("welcome"), targetPeer: bob.LocalPeerId);
        Assert.True(Pump(() => bobLog.Count == 21 && hostLog.Count == 20, host, alice, bob));
        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"alice:m{i}"), bobLog.Where(l => l.StartsWith("alice")));
        Assert.Contains("host:welcome", bobLog);
        Assert.Empty(aliceLog);

        // A message addressed to the host is not relayed.
        bob.Send(Encoding.UTF8.GetBytes("private"), targetPeer: 1);
        Assert.True(Pump(() => hostLog.Contains("bob:private"), host, alice, bob));
        DateTime settle = DateTime.UtcNow.AddMilliseconds(300);
        Pump(() => DateTime.UtcNow > settle, host, alice, bob);
        Assert.DoesNotContain("bob:private", aliceLog);
    }

    [Fact]
    public void ReplicatedNodesFollowTheOwnerSmoothly()
    {
        using Scene hostScene = new();
        using Scene clientScene = new();
        Node hostCube = hostScene.CreateNode(name: "cube");
        Node clientCube = clientScene.CreateNode(name: "cube");

        NetworkOptions options = new() { TickRate = 60, InterpolationDelay = TimeSpan.FromMilliseconds(50) };
        using NetworkSession host = NetworkSession.Host(0, options: options);
        using NetworkSession client = NetworkSession.Connect("127.0.0.1", host.Port, options: options);
        host.Replicate(hostCube, 7, owned: true);
        ReplicatedNode remote = client.Replicate(clientCube, 7, owned: false);

        Assert.True(Pump(() => client.IsConnected, host, client));

        // Move the host cube along x for a while; the client copy follows.
        DateTime start = DateTime.UtcNow;
        float lastX = 0f;
        bool monotonic = true;
        while ((DateTime.UtcNow - start).TotalMilliseconds < 800)
        {
            float t = (float)(DateTime.UtcNow - start).TotalSeconds;
            hostCube.Position = new Vector3(t * 10f, 1f, 0f);
            hostCube.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, t);
            host.Update();
            client.Update();
            float x = clientCube.Position.X;
            if (x + 0.01f < lastX)
            {
                monotonic = false;
            }

            lastX = x;
            Thread.Sleep(4);
        }

        Assert.True(remote.SnapshotCount > 10, $"snapshots: {remote.SnapshotCount}");
        Assert.True(monotonic, "interpolated motion should not jitter backwards");
        Assert.True(clientCube.Position.X > 5f, $"client x {clientCube.Position.X}");
        Assert.Equal(1f, clientCube.Position.Y, 3);

        // When the owner stops, the copy converges on the final pose.
        Vector3 final = hostCube.Position;
        Assert.True(Pump(() => Vector3.Distance(clientCube.Position, final) < 0.01f, host, client));
    }

    [Fact]
    public void DisconnectsAndTimeoutsAreReported()
    {
        NetworkOptions options = new() { Timeout = TimeSpan.FromMilliseconds(600), ConnectTimeout = TimeSpan.FromMilliseconds(500) };
        using NetworkSession host = NetworkSession.Host(0, options: options);
        NetworkSession client = NetworkSession.Connect("127.0.0.1", host.Port, options: options);
        int left = 0;
        host.PeerDisconnected += _ => left++;
        Assert.True(Pump(() => client.IsConnected && host.Peers.Count == 1, host, client));

        client.Dispose();
        Assert.True(Pump(() => left == 1 && host.Peers.Count == 0, host));

        // Nobody listens on this port: the client gives up.
        int closedPort;
        using (NetworkSession probe = NetworkSession.Host(0))
        {
            closedPort = probe.Port;
        }

        using NetworkSession lonely = NetworkSession.Connect("127.0.0.1", closedPort, options: options);
        string? reason = null;
        lonely.Closed += r => reason = r;
        Assert.True(Pump(() => lonely.IsClosed, lonely));
        Assert.Contains("timed out", reason);
        Assert.Throws<InvalidOperationException>(() => lonely.Send([1]));
    }
}
