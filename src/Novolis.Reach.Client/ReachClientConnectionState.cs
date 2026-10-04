using System.Net;
using System.Security.Cryptography;
using Novolis.Reach.Protocol;
using Novolis.Reach.Transport;
using Novolis.Transports;
using Novolis.Transports.Framing;
using Novolis.Transports.Udp;

namespace Novolis.Reach.Client;

/// <summary>Describes the observable connection state of a Reach client session.</summary>
public enum ReachClientConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Streaming,
    Lost,
}
