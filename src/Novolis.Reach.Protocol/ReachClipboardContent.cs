using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Clipboard content response.</summary>
public sealed record ReachClipboardContent(
    string Format,
    string? Text,
    string[]? Files = null,
    byte[]? Data = null);
