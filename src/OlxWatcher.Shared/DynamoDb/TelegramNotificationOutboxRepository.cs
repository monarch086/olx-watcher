using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using OlxWatcher.Shared.Dtos;

namespace OlxWatcher.Shared.DynamoDb;

public sealed class TelegramNotificationOutboxRepository
{
    private const string PendingStatus = "pending";
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public TelegramNotificationOutboxRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb;
        _tableName = tableName;
    }

    public Task EnqueueAsync(TelegramNotificationDto notification, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            ConditionExpression = "attribute_not_exists(notificationId)",
            Item = new Dictionary<string, AttributeValue>
            {
                ["notificationId"] = new() { S = notification.NotificationId },
                ["chatId"] = new() { S = notification.ChatId },
                ["text"] = new() { S = notification.Text },
                ["parseMode"] = notification.ParseMode is null ? new AttributeValue { NULL = true } : new AttributeValue { S = notification.ParseMode },
                ["disableWebPagePreview"] = new() { BOOL = notification.DisableWebPagePreview },
                ["status"] = new() { S = PendingStatus },
                ["createdAt"] = new() { S = now.ToString("O") },
                ["nextAttemptAt"] = new() { S = now.ToString("O") },
                ["attemptCount"] = new() { N = "0" }
            }
        }, cancellationToken);

    public async Task<IReadOnlyList<TelegramNotificationDto>> GetPendingAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var notifications = new List<TelegramNotificationDto>();
        Dictionary<string, AttributeValue>? lastEvaluatedKey = null;
        do
        {
            var response = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = _tableName,
                IndexName = "StatusNextAttemptAtIndex",
                KeyConditionExpression = "#status = :pending AND nextAttemptAt <= :now",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#status"] = "status" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pending"] = new() { S = PendingStatus },
                    [":now"] = new() { S = now.ToString("O") }
                },
                ExclusiveStartKey = lastEvaluatedKey
            }, cancellationToken);
            notifications.AddRange(response.Items.Select(ToNotification));
            lastEvaluatedKey = response.LastEvaluatedKey;
        }
        while (lastEvaluatedKey is { Count: > 0 });

        return notifications;
    }

    public Task MarkDeliveredAsync(string notificationId, CancellationToken cancellationToken = default) =>
        _dynamoDb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue> { ["notificationId"] = new() { S = notificationId } }
        }, cancellationToken);

    public Task ScheduleRetryAsync(string notificationId, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken = default) =>
        _dynamoDb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue> { ["notificationId"] = new() { S = notificationId } },
            UpdateExpression = "SET nextAttemptAt = :nextAttemptAt ADD attemptCount :increment",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":nextAttemptAt"] = new() { S = nextAttemptAt.ToString("O") },
                [":increment"] = new() { N = "1" }
            }
        }, cancellationToken);

    private static TelegramNotificationDto ToNotification(IReadOnlyDictionary<string, AttributeValue> item) => new()
    {
        NotificationId = item["notificationId"].S,
        ChatId = item["chatId"].S,
        Text = item["text"].S,
        ParseMode = item.TryGetValue("parseMode", out var parseMode) && parseMode.NULL != true ? parseMode.S : null,
        DisableWebPagePreview = item.TryGetValue("disableWebPagePreview", out var disablePreview) && disablePreview.BOOL == true
    };
}
