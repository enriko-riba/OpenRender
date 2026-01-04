using DarkVox.Server;
using DarkVox.Shared.Net;
using DarkVox.Shared.World;
using DarkVox.World;
using DarkVox.Shared.Gameplay;
using DarkVox.Shared.World.Registry;
using DarkVox.Shared.State;

namespace DarkVox.Client;

/// <summary>
/// Owns the client/server wiring for a local (in-process) session.
/// Program.cs owns creation; scenes only consume it.
/// </summary>
public sealed class GameSession(
    VoxelWorld world,
    LocalGameClient client,
    LocalGameServerHost host,
    PlayerId playerId,
    LocalGameServer server)
{
    public VoxelWorld World => world;
    public LocalGameClient Client => client;
    public LocalGameServerHost Host => host;
    public PlayerId PlayerId => playerId;
    
    /// <summary>Gets the performance metrics for terrain generation.</summary>
    public ChunkProcessingMetrics? Metrics => server.Metrics;

    public void Start()
    {
        Host.Start();
        Client.Start();
    }

    public void Stop()
    {
        try { Client.Stop(); } catch { }
        try { Host.Stop(); } catch { }
    }
}
