using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>One typed message envelope on a Reach control stream.</summary>
public sealed record ReachMessageEnvelope(
    ReachMessageType Type,
    long Sequence,
    JsonElement Body);
