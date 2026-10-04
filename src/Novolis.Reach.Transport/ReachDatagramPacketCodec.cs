using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Novolis.Reach.Transport;

/// <summary>
/// Encodes authenticated Reach media datagrams without IP fragmentation.
/// </summary>
public static class ReachDatagramPacketCodec
{
    /// <summary>Maximum packet size accepted by the Reach media path.</summary>
    public const int MaximumPacketSize = 1_200;

    /// <summary>Packet kind used for the unencrypted session handshake.</summary>
    public const byte HandshakeKind = 1;

    /// <summary>Packet kind used for authenticated media fragments.</summary>
    public const byte DataKind = 2;

    private const int HeaderLength = 36;
    private const int AuthenticationTagLength = 16;
    private const int NonceLength = 12;
    private const byte Version = 1;
    private static readonly byte[] Magic = "RCHD"u8.ToArray();

    /// <summary>Encodes one UDP session discovery handshake.</summary>
    public static byte[] EncodeHandshake(
        Guid sessionId,
        string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        if (tokenBytes.Length > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(token),
                "Reach UDP handshake tokens cannot exceed 255 bytes.");
        }

        var packet = new byte[4 + 1 + 1 + 16 + 1 + tokenBytes.Length];
        Magic.CopyTo(packet, 0);
        packet[4] = Version;
        packet[5] = HandshakeKind;
        sessionId.TryWriteBytes(packet.AsSpan(6, 16));
        packet[22] = (byte)tokenBytes.Length;
        tokenBytes.CopyTo(packet, 23);
        return packet;
    }

    /// <summary>Attempts to decode a session discovery handshake.</summary>
    public static bool TryDecodeHandshake(
        ReadOnlySpan<byte> packet,
        out Guid sessionId,
        out string token)
    {
        sessionId = default;
        token = string.Empty;
        if (packet.Length < 23
            || packet.Length > MaximumPacketSize
            || !packet[..4].SequenceEqual(Magic)
            || packet[4] != Version
            || packet[5] != HandshakeKind)
        {
            return false;
        }

        var tokenLength = packet[22];
        if (packet.Length != 23 + tokenLength)
            return false;

        sessionId = new Guid(packet.Slice(6, 16));
        token = Encoding.UTF8.GetString(packet.Slice(23, tokenLength));
        return token.Length > 0;
    }

    /// <summary>
    /// Splits and authenticates one application payload into bounded packets.
    /// </summary>
    public static IReadOnlyList<byte[]> EncodeData(
        ReachDatagramSession session,
        long sequence,
        ReadOnlySpan<byte> payload,
        int maximumPacketSize = MaximumPacketSize)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            maximumPacketSize,
            HeaderLength + AuthenticationTagLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maximumPacketSize,
            MaximumPacketSize);
        if (payload.Length == 0)
            throw new ArgumentException("A media payload cannot be empty.", nameof(payload));

        var maximumFragmentSize =
            maximumPacketSize - HeaderLength - AuthenticationTagLength;
        var fragmentCount = checked(
            (payload.Length + maximumFragmentSize - 1)
            / maximumFragmentSize);
        if (fragmentCount > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                "The Reach datagram payload has too many fragments.");
        }

        var packets = new byte[fragmentCount][];
        using var aes = new AesGcm(session.Key, AuthenticationTagLength);
        for (var index = 0; index < fragmentCount; index++)
        {
            var offset = index * maximumFragmentSize;
            var length = Math.Min(maximumFragmentSize, payload.Length - offset);
            var header = CreateHeader(
                session.SessionId,
                sequence,
                (ushort)index,
                (ushort)fragmentCount,
                (ushort)length);
            var ciphertext = new byte[length];
            var tag = new byte[AuthenticationTagLength];
            aes.Encrypt(
                CreateNonce(sequence, (ushort)index),
                payload.Slice(offset, length),
                ciphertext,
                tag,
                header);
            packets[index] = [.. header, .. ciphertext, .. tag];
        }

        return packets;
    }

    /// <summary>Attempts to authenticate and decode one media fragment.</summary>
    public static bool TryDecodeData(
        ReachDatagramSession session,
        ReadOnlySpan<byte> packet,
        out ReachDatagramFragment fragment)
    {
        fragment = default;
        ArgumentNullException.ThrowIfNull(session);
        session.Validate();
        if (packet.Length < HeaderLength + AuthenticationTagLength
            || packet.Length > MaximumPacketSize
            || !packet[..4].SequenceEqual(Magic)
            || packet[4] != Version
            || packet[5] != DataKind)
        {
            return false;
        }

        var sessionId = new Guid(packet.Slice(6, 16));
        if (sessionId != session.SessionId)
            return false;

        var sequence = BinaryPrimitives.ReadInt64BigEndian(packet[22..30]);
        if (sequence < 0)
            return false;
        var fragmentIndex = BinaryPrimitives.ReadUInt16BigEndian(packet[30..32]);
        var fragmentCount = BinaryPrimitives.ReadUInt16BigEndian(packet[32..34]);
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(packet[34..36]);
        if (fragmentCount == 0
            || fragmentIndex >= fragmentCount
            || payloadLength == 0
            || packet.Length != HeaderLength
                + payloadLength
                + AuthenticationTagLength)
        {
            return false;
        }

        var ciphertext = packet.Slice(HeaderLength, payloadLength);
        var tag = packet.Slice(HeaderLength + payloadLength, AuthenticationTagLength);
        var plaintext = new byte[payloadLength];
        try
        {
            using var aes = new AesGcm(session.Key, AuthenticationTagLength);
            aes.Decrypt(
                CreateNonce(sequence, fragmentIndex),
                ciphertext,
                tag,
                plaintext,
                packet[..HeaderLength]);
        }
        catch (CryptographicException)
        {
            return false;
        }

        fragment = new ReachDatagramFragment(
            sessionId,
            sequence,
            fragmentIndex,
            fragmentCount,
            plaintext);
        return true;
    }

    private static byte[] CreateHeader(
        Guid sessionId,
        long sequence,
        ushort fragmentIndex,
        ushort fragmentCount,
        ushort payloadLength)
    {
        var header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        header[4] = Version;
        header[5] = DataKind;
        sessionId.TryWriteBytes(header.AsSpan(6, 16));
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(22, 8), sequence);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(30, 2), fragmentIndex);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(32, 2), fragmentCount);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(34, 2), payloadLength);
        return header;
    }

    private static byte[] CreateNonce(long sequence, ushort fragmentIndex)
    {
        var nonce = new byte[NonceLength];
        BinaryPrimitives.WriteInt64BigEndian(nonce, sequence);
        BinaryPrimitives.WriteUInt16BigEndian(nonce.AsSpan(8, 2), fragmentIndex);
        nonce[10] = Version;
        nonce[11] = DataKind;
        return nonce;
    }
}
