using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Initial client identity and platform message.</summary>
public sealed record ReachClientHello(
    string AppId,
    string ProtocolVersion,
    ReachPlatform Platform,
    string ClientName);
