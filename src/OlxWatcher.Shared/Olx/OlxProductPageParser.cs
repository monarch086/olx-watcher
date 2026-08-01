using System.Text.Json;
using System.Text.RegularExpressions;
using OlxWatcher.Shared.Dtos;

namespace OlxWatcher.Shared.Olx;

public static class OlxProductPageParser
{
    private static readonly Regex JsonLdScriptRegex = new(
        "<script\\b(?<attributes>[^>]*)>(?<content>.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex ProductIdRegex = new(
        "\\\"(?:advertId|advert_id|ad_id|productId|product_id)\\\"\\s*:\\s*\\\"?(?<id>\\d+)\\\"?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsValidProductId(string value) =>
        Regex.IsMatch(value, "^\\d+$", RegexOptions.CultureInvariant);

    public static OlxProductDetailsDto? Parse(string html)
    {
        OlxProductDetailsDto? productDetails = null;
        foreach (Match match in JsonLdScriptRegex.Matches(html))
        {
            if (!match.Groups["attributes"].Value.Contains("application/ld+json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(match.Groups["content"].Value);
                var details = FindProduct(document.RootElement);
                if (details is not null)
                {
                    productDetails = details;
                    break;
                }
            }
            catch (JsonException)
            {
                // Ignore malformed unrelated JSON-LD blocks and continue searching.
            }
        }

        var fallbackProductId = ProductIdRegex.Match(html) is { Success: true } idMatch
            ? idMatch.Groups["id"].Value
            : null;
        if (productDetails is null)
        {
            return fallbackProductId is null ? null : new OlxProductDetailsDto { ProductId = fallbackProductId };
        }

        return productDetails.ProductId is not null || fallbackProductId is null
            ? productDetails
            : new OlxProductDetailsDto
            {
                ProductId = fallbackProductId,
                Name = productDetails.Name,
                Price = productDetails.Price,
                Currency = productDetails.Currency
            };
    }

    private static OlxProductDetailsDto? FindProduct(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (HasProductType(element))
            {
                var details = CreateProductDetails(element);
                if (details is not null)
                {
                    return details;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                var nestedProduct = FindProduct(property.Value);
                if (nestedProduct is not null)
                {
                    return nestedProduct;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nestedProduct = FindProduct(item);
                if (nestedProduct is not null)
                {
                    return nestedProduct;
                }
            }
        }

        return null;
    }

    private static bool HasProductType(JsonElement element) =>
        element.TryGetProperty("@type", out var type)
        && (type.ValueKind == JsonValueKind.String && string.Equals(type.GetString(), "Product", StringComparison.OrdinalIgnoreCase)
            || type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String
                && string.Equals(item.GetString(), "Product", StringComparison.OrdinalIgnoreCase)));

    private static OlxProductDetailsDto? CreateProductDetails(JsonElement product)
    {
        var name = GetJsonString(product, "name");
        var (price, currency) = TryGetOffer(product);
        var productId = new[] { "productID", "productId", "sku", "id" }
            .Select(propertyName => GetJsonString(product, propertyName))
            .FirstOrDefault(value => value is not null && IsValidProductId(value));

        return name is null && price is null && productId is null
            ? null
            : new OlxProductDetailsDto
            {
                ProductId = productId,
                Name = name,
                Price = price,
                Currency = currency
            };
    }

    private static (string? Price, string? Currency) TryGetOffer(JsonElement product)
    {
        if (!product.TryGetProperty("offers", out var offers))
        {
            return (null, null);
        }

        if (offers.ValueKind == JsonValueKind.Array)
        {
            offers = offers.EnumerateArray().FirstOrDefault();
        }

        return offers.ValueKind == JsonValueKind.Object
            ? (GetJsonString(offers, "price"), GetJsonString(offers, "priceCurrency"))
            : (null, null);
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }
}
