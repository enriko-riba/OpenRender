using DarkVox.Shared.Commands;
using DarkVox.Shared.Input;
using DarkVox.Shared.Net;
using DarkVox.Shared.State;

namespace DarkVox.Client;

public sealed class LocalGameClient(
    IClientConnection<IClientToServerMessage, IServerToClientMessage> connection,
    PlayerId playerId)
{
    private readonly object sync = new();
    private readonly object stateSync = new();
    private CancellationTokenSource? cts;
    private Task? loopTask;

    private readonly System.Collections.Concurrent.ConcurrentQueue<ServerChunkPayloadMessage> chunkPayloads = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<GameStateSnapshot> snapshots = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<MobStateSnapshot> mobSnapshots = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<WorldTimeSnapshot> worldTimeSnapshots = new();

    private LoadingProgressSnapshot? lastLoadingProgress;
    private GameStateSnapshot? lastSnapshot;
    private WorldTimeSnapshot? lastWorldTimeSnapshot;
    private bool hasServerGameStarted;

    public LoadingProgressSnapshot? LastLoadingProgress
    {
        get
        {
            lock (stateSync)
            {
                return lastLoadingProgress;
            }
        }
    }

    public GameStateSnapshot? LastSnapshot
    {
        get
        {
            lock (stateSync)
            {
                return lastSnapshot;
            }
        }
    }

    public bool HasServerGameStarted
    {
        get
        {
            lock (stateSync)
            {
                return hasServerGameStarted;
            }
        }
    }

    public PlayerId PlayerId => playerId;

    public void Connect() => connection.Send(new ClientHelloMessage(playerId));

    public bool TryDequeueChunkPayload(out ServerChunkPayloadMessage payload)
        => chunkPayloads.TryDequeue(out payload);

    public bool TryDequeueSnapshot(out GameStateSnapshot snapshot)
        => snapshots.TryDequeue(out snapshot);

    public bool TryDequeueMobSnapshot(out MobStateSnapshot snapshot)
        => mobSnapshots.TryDequeue(out snapshot);

    public bool TryDequeueWorldTimeSnapshot(out WorldTimeSnapshot snapshot)
        => worldTimeSnapshots.TryDequeue(out snapshot);

    public WorldTimeSnapshot? LastWorldTimeSnapshot
    {
        get
        {
            lock (stateSync)
            {
                return lastWorldTimeSnapshot;
            }
        }
    }

    public void SendInput(PlayerInputCommand input) => connection.Send(new ClientInputMessage(playerId, input));

    public void Send(BreakBlockCommand command) => connection.Send(new ClientBreakBlockMessage(playerId, command));

    public void Send(PlaceBlockCommand command) => connection.Send(new ClientPlaceBlockMessage(playerId, command));

    public void Send(EatFoodCommand command) => connection.Send(new ClientEatFoodMessage(playerId, command));

    public void Send(InventoryMoveCommand command) => connection.Send(new ClientInventoryMoveMessage(playerId, command));

    public void Send(ReturnToStorageCommand command) => connection.Send(new ClientReturnToStorageMessage(playerId, command));

    public void SendAttack(MobId targetMob) => connection.Send(new ClientAttackMobMessage(playerId, targetMob));

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
        // Block waiting for server updates; no busy spinning.
        var timeout = TimeSpan.FromMilliseconds(250);
        while (!token.IsCancellationRequested)
        {
            if (!connection.WaitReceive(timeout, out var message))
            {
                continue;
            }

            Handle(message);
            while (connection.TryReceive(out var msg))
            {
                Handle(msg);
            }
        }
    }

    private void Handle(IServerToClientMessage message)
    {
        switch (message)
        {
            case ServerStateMessage state:
                if (state.PlayerId.Equals(playerId))
                {
                    snapshots.Enqueue(state.Snapshot);
                    lock (stateSync)
                    {
                        lastSnapshot = state.Snapshot;
                    }
                }
                break;

            case ServerMobStateMessage mobs:
                if (mobs.PlayerId.Equals(playerId))
                {
                    mobSnapshots.Enqueue(mobs.Snapshot);
                }
                break;

            case ServerWorldTimeMessage time:
                if (time.PlayerId.Equals(playerId))
                {
                    worldTimeSnapshots.Enqueue(time.Snapshot);
                    lock (stateSync)
                    {
                        lastWorldTimeSnapshot = time.Snapshot;
                    }
                }
                break;

            case ServerLoadingProgressMessage progress:
                if (progress.PlayerId.Equals(playerId))
                {
                    lock (stateSync)
                    {
                        lastLoadingProgress = progress.Progress;
                    }
                }
                break;

            case ServerChunkPayloadMessage chunk:
                if (chunk.PlayerId.Equals(playerId))
                {
                    chunkPayloads.Enqueue(chunk);
                }
                break;

            case ServerGameStartMessage start:
                if (start.PlayerId.Equals(playerId))
                {
                    lock (stateSync)
                    {
                        hasServerGameStarted = true;
                    }
                }
                break;
        }
    }
}
