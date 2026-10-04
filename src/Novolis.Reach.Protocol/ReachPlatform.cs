using System.Text.Json;
using System.Text.Json.Serialization;

namespace Novolis.Reach.Protocol;

/// <summary>Operating system family of a Reach client or host.</summary>
public enum ReachPlatform
{
    Windows,
    Linux,
    Android,
}
