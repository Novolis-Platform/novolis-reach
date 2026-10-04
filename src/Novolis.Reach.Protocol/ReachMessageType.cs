using System.Text.Json;
using System.Text.Json.Serialization;

namespace Novolis.Reach.Protocol;

/// <summary>Message kinds carried by the Reach control channel.</summary>
public enum ReachMessageType
{
    HostHello,
    ClientHello,
    HostCapabilities,
    ClientCapabilities,
    SessionOpen,
    SessionResume,
    SessionClose,
    MediaHello,
    BulkHello,
    DisplayTopology,
    DisplaySelect,
    DisplayResize,
    PointerMove,
    PointerButton,
    PointerWheel,
    KeyDown,
    KeyUp,
    TextInput,
    ClipboardChanged,
    ClipboardContent,
    VideoStreamStart,
    VideoStreamConfiguration,
    VideoStreamReset,
    VideoFrame,
    RequestKeyFrame,
    AudioStreamStart,
    AudioStreamConfiguration,
    AudioFrame,
    FileOffer,
    FileChunk,
    FileComplete,
    DatagramOffer,
    HostStatus,
    LatencyProbe,
    LatencyResponse,
    SharingState,
}
