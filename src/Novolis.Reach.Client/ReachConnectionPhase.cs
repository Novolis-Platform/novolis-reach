using System.Net;
using System.Security.Cryptography;
using Novolis.Reach.Protocol;
using Novolis.Reach.Transport;
using Novolis.Transports;
using Novolis.Transports.Framing;
using Novolis.Transports.Udp;

namespace Novolis.Reach.Client;

/// <summary>Detailed phase shown by the Reach client surface.</summary>
public enum ReachConnectionPhase
{
    Disconnected,
    Discovering,
    Connecting,
    Authenticating,
    Starting,
    Streaming,
    Degraded,
    Reconnecting,
    Ended,
}
