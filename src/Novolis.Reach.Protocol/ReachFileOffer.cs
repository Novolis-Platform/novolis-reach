using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Offers a file for a later file-transfer phase.</summary>
public sealed record ReachFileOffer(Guid TransferId, string Name, long Length, string Hash);
