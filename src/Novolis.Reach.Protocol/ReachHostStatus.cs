using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Operator-facing host status.</summary>
public sealed record ReachHostStatus(
    string State,
    string[] Endpoints,
    int ConnectedClients,
    bool SharingPaused,
    string? InteractiveUser,
    string[] RecentMessages,
    ReachPerformanceSnapshot? Performance = null);
