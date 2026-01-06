using OpenTK.Mathematics;
using DarkVox.Server;
using DarkVox.Server.Streaming;
using DarkVox.Shared.Net;
using DarkVox.Shared.State;
using DarkVox.Shared.World;
using Xunit;

namespace DarkVox.Tests.Net;

public sealed class TimeSyncHandshakeTests
{
    [Fact]
    public void ClientHello_ShouldReceiveWorldTimeImmediately_BeforeFirstTick()
    {
        // Arrange
        var world = new VoxelWorld(seed: 123);
        var spawnPosition = new Vector3(0, 80, 0);
        var streamingManager = new ChunkStreamingManager(world);
        var server = new LocalGameServer(world, streamingManager, spawnPosition);

        var playerId = new PlayerId(Guid.NewGuid());
        var (clientConn, serverConn) = InMemoryDuplexConnection.CreatePair<IClientToServerMessage, IServerToClientMessage>();

        using var host = new LocalGameServerHost(server, serverConn, playerId)
        {
            // If the server only sent time during ticks, we'd have to wait ~1s.
            TickRateHz = 1.0
        };

        host.Start();

        // Act: handshake message.
        clientConn.Send(new ClientHelloMessage(playerId));

        // Assert: time should arrive promptly, without waiting for the first tick.
        Assert.True(clientConn.WaitReceive(TimeSpan.FromMilliseconds(200), out var msg));
        var timeMsg = Assert.IsType<ServerWorldTimeMessage>(msg);
        Assert.Equal(playerId, timeMsg.PlayerId);

        // At time 0, the server starts at 07:00 (per WorldTimeService default).
        Assert.Equal(7 * 60 * 60, timeMsg.Snapshot.TimeOfDaySeconds);
        Assert.Equal(0, timeMsg.Snapshot.DayIndex);
    }
}
