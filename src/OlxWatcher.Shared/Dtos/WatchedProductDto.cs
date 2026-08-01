namespace OlxWatcher.Shared.Dtos;

public sealed class WatchedProductDto
{
    public required string ChatId { get; init; }

    public required string ProductUrl { get; init; }

    public string? ProductId { get; init; }

    public string? ProductName { get; init; }

    public string? ProductPrice { get; init; }

    public bool? IsActive { get; init; }

    public DateTimeOffset? LastCheckedAt { get; init; }
}
