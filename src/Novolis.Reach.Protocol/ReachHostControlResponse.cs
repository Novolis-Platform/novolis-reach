using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Local IPC response from the host service.</summary>
public sealed record ReachHostControlResponse(
    bool Ok,
    string Message,
    ReachHostStatus? Status = null);
