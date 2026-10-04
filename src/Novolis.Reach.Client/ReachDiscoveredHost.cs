using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Novolis.Reach.Protocol;
using Novolis.Transports.Discovery;
using Novolis.Transports.Tailscale;

namespace Novolis.Reach.Client;

/// <summary>One Reach host returned by a local-network discovery scan.</summary>
public sealed record ReachDiscoveredHost(
    string HostName,
    string ProtocolVersion,
    string Endpoint);
