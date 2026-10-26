using System.Net;
using OlxWatcher.Shared.Dtos;

namespace OlxWatcher.Shared.Olx;

public sealed class OlxProductClient
{
    private readonly HttpClient _httpClient;

    public OlxProductClient(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<OlxProductDetailsDto?> GetProductDetailsAsync(string productUrl, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(productUrl, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return new OlxProductDetailsDto { IsActive = false };
        }

        return response.IsSuccessStatusCode
            ? OlxProductPageParser.Parse(await response.Content.ReadAsStringAsync(cancellationToken))
            : null;
    }
}
