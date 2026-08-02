namespace OlxWatcher.Shared.Dtos;

public sealed class TelegramNotificationDto
{
    public required string NotificationId { get; init; }

    public required string ChatId { get; init; }

    public required string Text { get; init; }

    public string? ParseMode { get; init; }

    public bool DisableWebPagePreview { get; init; }
}
