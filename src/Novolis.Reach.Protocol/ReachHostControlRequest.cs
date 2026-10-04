using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Local IPC request from the operator host app.</summary>
public sealed record ReachHostControlRequest(
    ReachHostCommand Command,
    bool? Enabled = null);
