using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Pointer move in selected-display coordinates.</summary>
public sealed record ReachPointerMove(double X, double Y);
