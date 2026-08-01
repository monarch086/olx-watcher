using System.Globalization;

namespace OlxWatcher.Shared;

public static class PriceFormatter
{
    private static readonly CultureInfo UkrainianCulture = CultureInfo.GetCultureInfo("uk-UA");

    public static bool TryParse(string? price, out decimal value) =>
        decimal.TryParse(price, NumberStyles.Number, CultureInfo.InvariantCulture, out value)
        || decimal.TryParse(price, NumberStyles.Number, UkrainianCulture, out value);

    public static string FormatAmount(decimal value) => value.ToString("N2", UkrainianCulture);

    public static string FormatUah(string price) =>
        TryParse(price, out var value) ? $"{FormatAmount(value)} UAH" : price.Trim();
}
