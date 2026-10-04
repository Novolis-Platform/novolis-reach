namespace Novolis.Reach.Transport;

/// <summary>Authenticated state for one Reach UDP media path.</summary>
public sealed record ReachDatagramSession(
    Guid SessionId,
    byte[] Key,
    string Token)
{
    /// <summary>Validates key and token material.</summary>
    public void Validate()
    {
        if (Key is not { Length: 16 or 24 or 32 })
        {
            throw new ArgumentException(
                "Reach datagram keys must be 128, 192, or 256 bits.",
                nameof(Key));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Token);
    }
}
