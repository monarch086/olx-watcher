using OlxWatcher.Shared.Dtos;

namespace OlxWatcher.Shared.Olx;

public sealed class OlxProductClient
{
    private readonly HttpClient _httpClient;

    public OlxProductClient(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<OlxProductDetailsDto?> GetProductDetailsAsync(string productUrl, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(productUrl, cancellationToken);
        return response.IsSuccessStatusCode
            ? OlxProductPageParser.Parse(await response.Content.ReadAsStringAsync(cancellationToken))
            : null;
    }
}
