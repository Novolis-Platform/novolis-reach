using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Novolis.Reach.Protocol;
using Novolis.Transports.Discovery;
using Novolis.Transports.Tailscale;

namespace Novolis.Reach.Client;

/// <summary>Finds Reach hosts on LAN broadcast and Tailscale interfaces.</summary>
public static class ReachClientDiscovery
{
    /// <summary>
    /// Sends the product-neutral discovery probe to local broadcast targets and
    /// returns compatible Reach endpoints found during the scan.
    /// </summary>
    public static async Task<IReadOnlyList<ReachDiscoveredHost>> ScanAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var targets = GetProbeTargets();
        var scans = targets.Select(target =>
            ScanTargetAsync(target, timeout, cancellationToken));
        var results = await Task.WhenAll(scans).ConfigureAwait(false);

        return results
            .SelectMany(static result => result)
            .DistinctBy(static host => host.Endpoint, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static host => host.HostName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static host => host.Endpoint, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<IReadOnlyList<ReachDiscoveredHost>> ScanTargetAsync(
        IPEndPoint target,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var hosts = new List<ReachDiscoveredHost>();
        try
        {
            var scanner = new DiscoveryScanner();
            await foreach (var beacon in scanner.ScanAsync(
                               target,
                               ReachProtocol.DiscoveryProbe,
                               timeout,
                               cancellationToken))
            {
                if (!string.Equals(
                        beacon.ApplicationId,
                        ReachProtocol.AppId,
                        StringComparison.Ordinal)
                    || !ReachProtocol.IsCompatible(beacon.ProtocolVersion))
                {
                    continue;
                }

                foreach (var endpoint in beacon.Endpoints)
                {
                    if (TryNormalizeEndpoint(endpoint, out var normalized))
                    {
                        hosts.Add(new ReachDiscoveredHost(
                            beacon.HostName,
                            beacon.ProtocolVersion,
                            normalized));
                    }
                }
            }
        }
        catch (SocketException)
        {
            // A disabled or isolated adapter must not prevent other scans.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        return hosts;
    }

    private static IReadOnlyList<IPEndPoint> GetProbeTargets()
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var endpoints = new List<IPEndPoint>
        {
            new(IPAddress.Loopback, ReachProtocol.DiscoveryPort),
            new(IPAddress.Broadcast, ReachProtocol.DiscoveryPort),
        };

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
                continue;

            foreach (var unicast in networkInterface
                         .GetIPProperties()
                         .UnicastAddresses
                         .Where(static item =>
                             item.Address.AddressFamily == AddressFamily.InterNetwork
                             && IsPrivateOrTailscaleIPv4(item.Address)))
            {
                var address = unicast.Address;
                AddTarget(endpoints, targets, address);

                if (unicast.IPv4Mask is not null)
                {
                    var broadcast = GetBroadcastAddress(address, unicast.IPv4Mask);
                    AddTarget(endpoints, targets, broadcast);
                }
            }
        }

        return endpoints;
    }

    private static void AddTarget(
        ICollection<IPEndPoint> endpoints,
        ISet<string> targets,
        IPAddress address)
    {
        var key = address + ":" + ReachProtocol.DiscoveryPort;
        if (targets.Add(key))
            endpoints.Add(new IPEndPoint(address, ReachProtocol.DiscoveryPort));
    }

    private static IPAddress GetBroadcastAddress(
        IPAddress address,
        IPAddress mask)
    {
        var addressBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        var broadcastBytes = new byte[addressBytes.Length];
        for (var index = 0; index < broadcastBytes.Length; index++)
            broadcastBytes[index] = (byte)(addressBytes[index] | ~maskBytes[index]);

        return new IPAddress(broadcastBytes);
    }

    private static bool TryNormalizeEndpoint(
        string endpoint,
        out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(
                endpoint.Contains("://", StringComparison.Ordinal)
                    ? endpoint
                    : $"tcp://{endpoint}",
                UriKind.Absolute,
                out var uri)
            || uri.Port <= 0)
        {
            return false;
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("tcp" or "quic"))
            return false;

        var host = uri.Host;
        if (OperatingSystem.IsAndroid()
            && (IPAddress.TryParse(host, out var address)
                && IPAddress.IsLoopback(address)
                || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)))
        {
            // The Android emulator reaches the Windows host through this
            // gateway address; a host's loopback beacon is otherwise resolved
            // inside the Android guest.
            host = "10.0.2.2";
        }

        normalized = $"{scheme}://{host}:{uri.Port}{uri.Query}";
        return true;
    }

    private static bool IsPrivateOrTailscaleIPv4(IPAddress address)
    {
        if (TailscaleAddressEnumerator.IsTailscaleIPv4(address))
            return true;

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254);
    }
}
