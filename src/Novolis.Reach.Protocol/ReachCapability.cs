using System.Text.Json;
using System.Text.Json.Serialization;

namespace Novolis.Reach.Protocol;

/// <summary>Capabilities that can be negotiated by a host and client.</summary>
[Flags]
public enum ReachCapability
{
    None = 0,
    H264 = 1 << 0,
    Av1 = 1 << 1,
    Touch = 1 << 2,
    Mouse = 1 << 3,
    Keyboard = 1 << 4,
    ClipboardText = 1 << 5,
    ClipboardFiles = 1 << 6,
    ClipboardImages = 1 << 7,
    SingleDisplay = 1 << 8,
    MultiMonitor = 1 << 9,
    Audio = 1 << 10,
    FileTransfer = 1 << 11,
    AdaptiveQuality = 1 << 12,
}
