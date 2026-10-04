using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Serializes typed Reach control envelopes without choosing a transport.</summary>
public static class ReachMessageCodec
{
    /// <summary>Creates UTF-8 JSON bytes for a typed message.</summary>
    public static byte[] Serialize<T>(
        ReachMessageType type,
        long sequence,
        T message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = JsonSerializer.SerializeToElement(message, ReachProtocol.JsonOptions);
        var envelope = new ReachMessageEnvelope(type, sequence, body);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, ReachProtocol.JsonOptions);
    }

    /// <summary>Reads an envelope from UTF-8 JSON bytes.</summary>
    public static ReachMessageEnvelope Deserialize(ReadOnlySpan<byte> payload)
    {
        var envelope = JsonSerializer.Deserialize<ReachMessageEnvelope>(
            payload,
            ReachProtocol.JsonOptions);
        return envelope ?? throw new InvalidDataException("Reach message was empty.");
    }

    /// <summary>Reads the typed body of an envelope.</summary>
    public static T ReadBody<T>(ReachMessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return envelope.Body.Deserialize<T>(ReachProtocol.JsonOptions)
            ?? throw new InvalidDataException($"Reach message {envelope.Type} had no body.");
    }

    /// <summary>Reads a body while enforcing the expected envelope type.</summary>
    public static T ReadBody<T>(
        ReachMessageEnvelope envelope,
        ReachMessageType expectedType)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Type != expectedType)
        {
            throw new InvalidDataException(
                $"Expected Reach message {expectedType}, received {envelope.Type}.");
        }

        return ReadBody<T>(envelope);
    }
}
