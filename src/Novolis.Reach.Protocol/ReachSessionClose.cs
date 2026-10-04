using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Closes a client session.</summary>
public sealed record ReachSessionClose(Guid SessionId, string Reason);
