using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Clipboard notification.</summary>
public sealed record ReachClipboardChanged(string Format);
