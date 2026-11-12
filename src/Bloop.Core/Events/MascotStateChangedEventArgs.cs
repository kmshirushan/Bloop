using Bloop.Core.Personality;

namespace Bloop.Core.Events;

public sealed record MascotStateChangedEventArgs(
    MascotState PreviousState,
    MascotState CurrentState,
    DateTimeOffset Timestamp
);

