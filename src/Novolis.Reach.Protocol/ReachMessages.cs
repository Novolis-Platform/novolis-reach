using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Initial host identity and endpoint message.</summary>
public sealed record ReachHostHello(
    string AppId,
    string ProtocolVersion,
    string HostName,
    string[] Endpoints);
