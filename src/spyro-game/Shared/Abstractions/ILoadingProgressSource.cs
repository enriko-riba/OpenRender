using SpyroGame.Shared.State;

namespace SpyroGame.Shared.Abstractions;

public interface ILoadingProgressSource
{
    bool TryGetLoadingProgress(PlayerId playerId, out LoadingProgressSnapshot progress);
}
