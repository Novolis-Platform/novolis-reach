using System.Text.Json;
using System.Text.Json.Serialization;

namespace Novolis.Reach.Protocol;

/// <summary>Lifecycle state of a Reach session.</summary>
public enum ReachSessionState
{
    New,
    HelloExchanged,
    Opening,
    Open,
    Resuming,
    Closing,
    Closed,
    Failed,
}
