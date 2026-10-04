using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Pointer button event.</summary>
public sealed record ReachPointerButton(string Button, bool IsDown, int ClickCount = 1);
