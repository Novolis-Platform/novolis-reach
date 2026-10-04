using System.Text.Json;
using System.Text.Json.Serialization;

namespace Novolis.Reach.Protocol;

/// <summary>Logical channels in a Reach session.</summary>
public enum ReachChannel
{
    Control,
    Input,
    Clipboard,
    File,
    Video,
    Audio,
}
