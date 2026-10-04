using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>
/// Offers an authenticated, loss-tolerant UDP media path over the secure
/// control channel.
/// </summary>
public sealed record ReachDatagramOffer(
    Guid SessionId,
    int Port,
    string Token,
    string Key,
    int MaximumPacketSize = 1_200);
