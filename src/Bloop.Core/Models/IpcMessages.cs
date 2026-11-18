using System.Text.Json;
using System.Text.Json.Serialization;
using Bloop.Core.Appearance;

namespace Bloop.Core.Models;

/// <summary>
/// Small, typed JSON message envelope shared between the WPF host and the Three.js renderer.
/// </summary>
public sealed record IpcMessage<TPayload>(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("timestamp")] long Timestamp,
    [property: JsonPropertyName("payload")] TPayload Payload
);

public sealed record RawIpcMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("timestamp")] long Timestamp,
    [property: JsonPropertyName("payload")] JsonElement Payload
);

public sealed record PointerCoordinatesPayload(
    [property: JsonPropertyName("screenX")] double ScreenX,
    [property: JsonPropertyName("screenY")] double ScreenY,
    [property: JsonPropertyName("velocityX")] double VelocityX = 0,
    [property: JsonPropertyName("velocityY")] double VelocityY = 0
);

public sealed record HostCursorPayload(
    [property: JsonPropertyName("normX")] double NormX,
    [property: JsonPropertyName("normY")] double NormY,
    [property: JsonPropertyName("isInsideWindow")] bool IsInsideWindow
);

public sealed record HostStatePayload(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("priority")] int Priority,
    [property: JsonPropertyName("commandId")] string? CommandId = null
);

public sealed record AnimationAckPayload(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("commandId")] string? CommandId
);

public sealed record HostDockPreviewPayload(
    [property: JsonPropertyName("edge")] string Edge
);

public sealed record HostHopStartPayload(
    [property: JsonPropertyName("edge")] string Edge,
    [property: JsonPropertyName("targetOffsetX")] double TargetOffsetX,
    [property: JsonPropertyName("targetOffsetY")] double TargetOffsetY,
    [property: JsonPropertyName("directionX")] double DirectionX,
    [property: JsonPropertyName("directionY")] double DirectionY,
    [property: JsonPropertyName("anticipationMs")] int AnticipationMs,
    [property: JsonPropertyName("flightMs")] int FlightMs,
    [property: JsonPropertyName("landingMs")] int LandingMs
);

public sealed record HostDockConfigPayload(
    [property: JsonPropertyName("edge")] string Edge,
    [property: JsonPropertyName("offsetX")] double OffsetX,
    [property: JsonPropertyName("offsetY")] double OffsetY,
    [property: JsonPropertyName("visibleRatio")] double VisibleRatio
);

public sealed record HostPeekabooPayload(
    [property: JsonPropertyName("edge")] string Edge,
    [property: JsonPropertyName("durationMs")] int DurationMs
);

public sealed record HostEdgeBalancePayload(
    [property: JsonPropertyName("edge")] string Edge,
    [property: JsonPropertyName("durationMs")] int DurationMs
);

public sealed record HostNotificationBadgePayload(
    [property: JsonPropertyName("unreadCount")] int UnreadCount
);

public sealed record HostNotificationReactionPayload(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("unreadCount")] int UnreadCount,
    [property: JsonPropertyName("appDisplayName")] string? AppDisplayName = null,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("commandId")] string? CommandId = null
);

public sealed record HostAppearancePayload(
    [property: JsonPropertyName("profileId")] string ProfileId,
    [property: JsonPropertyName("character")] CharacterAppearance Character,
    [property: JsonPropertyName("isPreview")] bool IsPreview = false
);

public sealed record HostAppearanceScalePayload(
    [property: JsonPropertyName("characterScale")] double CharacterScale
);

public sealed record HostAccessoryUpdatePayload(
    [property: JsonPropertyName("accessoryId")] string AccessoryId,
    [property: JsonPropertyName("equip")] bool Equip,
    [property: JsonPropertyName("accessoryTintColor")] string? AccessoryTintColor = null
);

public sealed record AppearanceAckPayload(
    [property: JsonPropertyName("profileId")] string ProfileId,
    [property: JsonPropertyName("preset")] string Preset,
    [property: JsonPropertyName("characterScale")] double CharacterScale,
    [property: JsonPropertyName("equippedAccessories")] IReadOnlyList<string> EquippedAccessories
);

public sealed record HostAwarenessStatePayload(
    [property: JsonPropertyName("context")] string Context,
    [property: JsonPropertyName("isHidden")] bool IsHidden,
    [property: JsonPropertyName("isQuietMode")] bool IsQuietMode,
    [property: JsonPropertyName("reason")] string Reason
);

public sealed record MascotPreferences(
    double WindowLeft,
    double WindowTop,
    bool AlwaysOnTop = true
);

