using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace FikaServer.Models.Fika.SendItem;

// Written before either profile is changed. Keep completed records so an HTTP
// retry with the same inventory roots can return the original result.
public sealed record SendItemTransferRecord
{
    public required string Id { get; init; }
    public required string SenderId { get; init; }
    public required string TargetId { get; init; }
    public required string SenderNickname { get; init; }
    public required string[] RootItemIds { get; init; }
    public required List<Item> Items { get; init; }
    public required long StorageSeconds { get; init; }
    public string Stage { get; set; } = "Prepared";
}
