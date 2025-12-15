using SpyroGame.Shared.Abstractions;
using SpyroGame.Shared.Net;
using SpyroGame.Shared.State;
using System.Diagnostics;
using System.Threading;

namespace SpyroGame.Server;

/// <summary>
/// Runs a server implementation against a transport. This is the seam where UDP/TCP can be plugged in later.
/// </summary>
public sealed class LocalGameServerHost(
    IGameServer server,
    IServerConnection<IClientToServerMessage, IServerToClientMessage> connection,
    PlayerId playerId) : IDisposable
{
    private readonly object sync = new();
    private CancellationTokenSource? cts;
    private Task? loopTask;

    public double TickRateHz { get; set; } = 60.0;

    private const int MaxChunkPayloadsPerTick = 16;

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
        var tickInterval = TimeSpan.FromSeconds(1.0 / tickHz);
        var tickSeconds = tickInterval.TotalSeconds;

        var stopwatch = Stopwatch.StartNew();
        var lastTime = stopwatch.Elapsed;
        var accumulator = TimeSpan.Zero;
        var maxCatchUp = TimeSpan.FromSeconds(0.25);

        PlayerSnapshot? lastSentPlayer = null;
        ChunkDeltaSnapshot? lastSentChunkDelta = null;

        while (!token.IsCancellationRequested)
        {
            var now = stopwatch.Elapsed;
            var frameTime = now - lastTime;
            lastTime = now;

            // Clamp to avoid spiral-of-death if paused/broken.
            if (frameTime > maxCatchUp)
            {
                frameTime = maxCatchUp;
            }

            accumulator += frameTime;
            if (accumulator > maxCatchUp)
            {
                accumulator = maxCatchUp;
            }

            // Wait up to the time remaining until the next tick (or return early on messages).
            var timeUntilTick = tickInterval - accumulator;
            if (timeUntilTick > TimeSpan.Zero)
            {
                if (connection.WaitReceive(timeUntilTick, out var firstMsg))
                {
                    HandleMessage(firstMsg);
                }
            }

            while (connection.TryReceive(out var msg))
            {
                HandleMessage(msg);
            }

            var ticked = false;
            while (accumulator >= tickInterval)
            {
                server.Tick(tickSeconds);
                accumulator -= tickInterval;
                ticked = true;
            }

            if (!ticked)
            {
                continue;
            }

            server.TryGetSnapshot(playerId, out var snapshot);

            // Avoid sending state when nothing changed (idle).
            var hasChunkChanges = snapshot.ChunkDelta is { HasChanges: true };
            var chunkChanged = hasChunkChanges && (lastSentChunkDelta is null || !lastSentChunkDelta.Value.Equals(snapshot.ChunkDelta!.Value));
            if (chunkChanged || lastSentPlayer is null || !lastSentPlayer.Value.Equals(snapshot.Player))
            {
                lastSentPlayer = snapshot.Player;
                lastSentChunkDelta = snapshot.ChunkDelta;
                connection.Send(new ServerStateMessage(playerId, snapshot));
            }

            // Send chunk voxel payloads for any loaded/changed chunks.
            if (server is IChunkPayloadSource payloadSource)
            {
                var payloads = new List<(int ChunkIndex, byte[] Payload)>(capacity: 8);
                for (var i = 0; i < MaxChunkPayloadsPerTick; i++)
                {
                    if (!payloadSource.TryDequeueOutgoingChunkPayload(playerId, out var idx, out var bytes))
                    {
                        break;
                    }

                    payloads.Add((idx, bytes));
                }

                foreach (var p in payloads)
                {
                    connection.Send(new ServerChunkPayloadMessage(playerId, p.ChunkIndex, p.Payload));
                }
            }

            // Send periodic loading progress updates.
            if (server is ILoadingProgressSource progressSource)
            {
                LoadingProgressSnapshot progress = default;
                var hasProgress = false;

                hasProgress = progressSource.TryGetLoadingProgress(playerId, out progress);

                if (hasProgress)
                {
                    connection.Send(new ServerLoadingProgressMessage(playerId, progress));
                }
            }

            // Game start signal once initial chunks have been sent.
            if (server is LocalGameServer concrete && concrete.ShouldSendGameStart(playerId))
            {
                connection.Send(new ServerGameStartMessage(playerId));
            }
        }
    }

    private void HandleMessage(IClientToServerMessage msg)
    {
        switch (msg)
        {
            case ClientHelloMessage hello:
                if (hello.PlayerId.Equals(playerId) && server is LocalGameServer concrete)
                {
                    concrete.Connect(hello.PlayerId);
                }
                break;
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
