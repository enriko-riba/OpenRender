using SpyroGame.Shared.Commands;
using SpyroGame.Shared.Input;
using SpyroGame.Shared.Net;
using SpyroGame.Shared.State;

namespace SpyroGame.Client;

public sealed class LocalGameClient(
    IClientConnection<IClientToServerMessage, IServerToClientMessage> connection,
    PlayerId playerId)
{
    private readonly object sync = new();
    private CancellationTokenSource? cts;
    private Task? loopTask;

    public GameStateSnapshot? LastSnapshot { get; private set; }

    public PlayerId PlayerId => playerId;

    public void SendInput(PlayerInputCommand input) => connection.Send(new ClientInputMessage(playerId, input));

    public void Send(BreakBlockCommand command) => connection.Send(new ClientBreakBlockMessage(playerId, command));

    public void Send(PlaceBlockCommand command) => connection.Send(new ClientPlaceBlockMessage(playerId, command));

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
                    LastSnapshot = state.Snapshot;
                }
                break;
        }
    }
}
