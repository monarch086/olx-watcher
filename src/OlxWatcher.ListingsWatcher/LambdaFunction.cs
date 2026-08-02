using Amazon.Lambda.CloudWatchEvents;
using Amazon.Lambda.Core;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace OlxWatcher.ListingsWatcher;

public sealed class Function
{
    private readonly ListingsWatcherService _watcherService = new();

    public Task FunctionHandler(CloudWatchEvent<object> scheduledEvent, ILambdaContext context) =>
        _watcherService.RunAsync(scheduledEvent, context);
}
