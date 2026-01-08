using DarkVox.Shared.Net;
using DarkVox.Shared.State;

namespace DarkVox.Server.Combat;

public interface ICombatEventSource
{
    bool TryDequeueCombatEvent(PlayerId playerId, out CombatEventSnapshot combatEvent);
}
