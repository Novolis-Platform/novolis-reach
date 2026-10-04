using System.Net;
using System.Net.Sockets;

namespace Novolis.Reach.Transport;

/// <summary>Parsed Reach endpoint with optional QUIC certificate pinning.</summary>
public sealed record ReachTransportEndpoint(
    string Scheme,
    IPEndPoint Address,
    string? CertificatePin)
{
    /// <summary>Gets whether this endpoint uses QUIC.</summary>
    public bool IsQuic =>
        string.Equals(Scheme, "quic", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets whether this endpoint uses TCP.</summary>
    public bool IsTcp =>
        string.Equals(Scheme, "tcp", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses a TCP or QUIC Reach endpoint.</summary>
    public static ReachTransportEndpoint Parse(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (!Uri.TryCreate(
                endpoint.Contains("://", StringComparison.Ordinal)
                    ? endpoint
                    : $"tcp://{endpoint}",
                UriKind.Absolute,
                out var uri)
            || uri.Port <= 0)
        {
            throw new FormatException($"Invalid Reach endpoint: {endpoint}");
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("tcp" or "quic"))
        {
            throw new FormatException(
                $"Reach endpoint scheme '{uri.Scheme}' is not supported.");
        }

        var address = IPAddress.TryParse(uri.Host, out var parsed)
            ? parsed
            : Dns.GetHostAddresses(uri.Host)
                .First(static item => item.AddressFamily == AddressFamily.InterNetwork);
        var pin = ParsePin(uri.Query);
        return new ReachTransportEndpoint(
            scheme,
            new IPEndPoint(address, uri.Port),
            pin);
    }

    /// <summary>Formats the endpoint as a Reach connection URI.</summary>
    public override string ToString()
    {
        var value = $"{Scheme}://{Address.Address}:{Address.Port}";
        return CertificatePin is { Length: > 0 }
            ? $"{value}?pin={Uri.EscapeDataString(CertificatePin)}"
            : value;
    }

    private static string? ParsePin(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2
                && string.Equals(parts[0], "pin", StringComparison.OrdinalIgnoreCase))
            {
                var pin = Uri.UnescapeDataString(parts[1]).Trim();
                if (pin.Length == 0)
                    return null;
                if (pin.Any(static character => !Uri.IsHexDigit(character)))
                {
                    throw new FormatException(
                        "The QUIC certificate pin must be hexadecimal.");
                }

                return pin;
            }
        }

        return null;
    }
}
