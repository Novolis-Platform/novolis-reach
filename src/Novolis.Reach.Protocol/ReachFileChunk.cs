using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Carries one file-transfer chunk.</summary>
public sealed record ReachFileChunk(Guid TransferId, long Offset, byte[] Data);
