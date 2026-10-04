using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Novolis.Reach.Transport;

/// <summary>One authenticated fragment of a Reach media payload.</summary>
public readonly record struct ReachDatagramFragment(
    Guid SessionId,
    long Sequence,
    ushort FragmentIndex,
    ushort FragmentCount,
    byte[] Payload);
