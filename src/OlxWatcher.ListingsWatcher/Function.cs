using Amazon.Lambda.CloudWatchEvents;
using Amazon.Lambda.Core;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace OlxWatcher.ListingsWatcher;

public sealed class Function
{
    public Task FunctionHandler(CloudWatchEvent<object> scheduledEvent, ILambdaContext context)
    {
        context.Logger.LogInformation($"Starting scheduled listing check at {DateTimeOffset.UtcNow:O}.");

        // Add the OLX query and notification workflow here.
        return Task.CompletedTask;
    }
}
