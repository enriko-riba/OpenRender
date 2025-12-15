using SpyroGame.Server;
using SpyroGame.Shared.Net;
using SpyroGame.Shared.State;
using SpyroGame.World;

namespace SpyroGame.Client;

/// <summary>
/// Owns the client/server wiring for a local (in-process) session.
/// Program.cs owns creation; scenes only consume it.
/// </summary>
public sealed class GameSession(
    VoxelWorld world,
    LocalGameClient client,
    LocalGameServerHost host,
    PlayerId playerId)
{
    public VoxelWorld World => world;
    public LocalGameClient Client => client;
    public LocalGameServerHost Host => host;
    public PlayerId PlayerId => playerId;

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
