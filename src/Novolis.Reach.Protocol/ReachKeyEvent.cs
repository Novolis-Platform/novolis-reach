using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Virtual-key event.</summary>
public sealed record ReachKeyEvent(ushort VirtualKey);
