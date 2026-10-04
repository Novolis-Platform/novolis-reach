using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Identifies an optional reliable bulk stream for an open session.</summary>
public sealed record ReachBulkHello(
    Guid SessionId,
    string AppId,
    string ProtocolVersion);
