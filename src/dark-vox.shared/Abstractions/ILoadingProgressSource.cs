using DarkVox.Shared.State;

namespace DarkVox.Shared.Abstractions;

public interface ILoadingProgressSource
{
    bool TryGetLoadingProgress(PlayerId playerId, out LoadingProgressSnapshot progress);
}
