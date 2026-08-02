using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace OlxWatcher.Shared.DynamoDb;

public sealed class ProductPriceHistoryRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public ProductPriceHistoryRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb;
        _tableName = tableName;
    }

    public Task RecordAsync(string productId, string productPrice, DateTimeOffset changeDate, CancellationToken cancellationToken = default) =>
        _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                ["productId"] = new() { S = productId },
                ["productPrice"] = new() { S = productPrice },
                ["changeDate"] = new() { S = changeDate.ToString("O") }
            }
        }, cancellationToken);
}
