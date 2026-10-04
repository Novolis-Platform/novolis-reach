using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Capability offer or negotiated capability set.</summary>
public sealed record ReachCapabilitiesMessage(ReachCapabilities Capabilities);
