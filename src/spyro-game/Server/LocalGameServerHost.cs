using SpyroGame.Shared.Abstractions;
using SpyroGame.Shared.Net;
using SpyroGame.Shared.State;
using System.Threading;

namespace SpyroGame.Server;

/// <summary>
/// Runs a server implementation against a transport. This is the seam where UDP/TCP can be plugged in later.
/// </summary>
public sealed class LocalGameServerHost(
    IGameServer server,
    IServerConnection<IClientToServerMessage, IServerToClientMessage> connection,
    Action<Action> scheduleOnMainThread,
    PlayerId playerId) : IDisposable
{
    private readonly object sync = new();
    private CancellationTokenSource? cts;
    private Task? loopTask;

    public double TickRateHz { get; set; } = 60.0;

    public void Start()
    {
        lock (sync)
        {
            if (loopTask != null) return;
            cts = new CancellationTokenSource();
            loopTask = Task.Run(() => RunLoop(cts.Token), cts.Token);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? localCts;
        Task? localTask;

        lock (sync)
        {
            localCts = cts;
            localTask = loopTask;
            cts = null;
            loopTask = null;
        }

        if (localCts != null)
        {
            try { localCts.Cancel(); } catch { }
            localCts.Dispose();
        }

        if (localTask != null)
        {
            try { localTask.Wait(TimeSpan.FromSeconds(1)); } catch { }
        }
    }

    private void RunLoop(CancellationToken token)
    {
        var tickHz = Math.Clamp(TickRateHz, 1.0, 240.0);
        var tickSeconds = 1.0 / tickHz;
        var tickTimeout = TimeSpan.FromSeconds(tickSeconds);

        PlayerSnapshot? lastSentPlayer = null;
        ChunkDeltaSnapshot? lastSentChunkDelta = null;

        while (!token.IsCancellationRequested)
        {
            // Wait for new messages, but tick at a fixed cadence.
            if (connection.WaitReceive(tickTimeout, out var firstMsg))
            {
                InvokeOnMainThread(() => HandleMessage(firstMsg));
                while (connection.TryReceive(out var msg))
                {
                    InvokeOnMainThread(() => HandleMessage(msg));
                }
            }

            GameStateSnapshot snapshot = default;
            InvokeOnMainThread(() =>
            {
                server.Tick(tickSeconds);
                server.TryGetSnapshot(playerId, out snapshot);
            });

            // Avoid sending state when nothing changed (idle).
            var hasChunkChanges = snapshot.ChunkDelta is { HasChanges: true };
            var chunkChanged = hasChunkChanges && (lastSentChunkDelta is null || !lastSentChunkDelta.Value.Equals(snapshot.ChunkDelta!.Value));
            if (chunkChanged || lastSentPlayer is null || !lastSentPlayer.Value.Equals(snapshot.Player))
            {
                lastSentPlayer = snapshot.Player;
                lastSentChunkDelta = snapshot.ChunkDelta;
                connection.Send(new ServerStateMessage(playerId, snapshot));
            }
        }
    }

    private void InvokeOnMainThread(Action action)
    {
        // Schedule the work on the main thread (render thread) and block until it completes.
        // This keeps server-side world/terrain logic safe while still allowing the transport to be threaded.
        using var done = new ManualResetEventSlim(false);
        Exception? error = null;
        scheduleOnMainThread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait();
        if (error != null) throw error;
    }

    private void HandleMessage(IClientToServerMessage msg)
    {
        switch (msg)
        {
            case ClientInputMessage input:
                if (input.PlayerId.Equals(playerId))
                    server.Submit(input.PlayerId, input.Input);
                break;
            case ClientBreakBlockMessage breakBlock:
                if (breakBlock.PlayerId.Equals(playerId))
                    server.Submit(breakBlock.PlayerId, breakBlock.Command);
                break;
            case ClientPlaceBlockMessage placeBlock:
                if (placeBlock.PlayerId.Equals(playerId))
                    server.Submit(placeBlock.PlayerId, placeBlock.Command);
                break;
        }
    }

    public void Dispose() => Stop();
}
