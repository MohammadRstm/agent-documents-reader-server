using System.Text.RegularExpressions;

namespace DocumentReader.Api.Ingestion.Excel;

/// <summary>
/// Finds currency hints in Excel number formats and header text. It reports what the file states:
/// an ISO code when one is explicit or the symbol is unambiguous (€, £, ₹), otherwise the symbol
/// itself (for example "$", which could mean USD, CAD, AUD and others). It never guesses.
/// </summary>
public static partial class CurrencyDetector
{
    private static readonly HashSet<string> IsoCodes = new(StringComparer.Ordinal)
    {
        "USD", "EUR", "GBP", "AED", "SAR", "QAR", "KWD", "BHD", "OMR", "JOD", "EGP", "LBP", "IQD", "TRY",
        "INR", "PKR", "CNY", "JPY", "CHF", "CAD", "AUD", "NZD", "SGD", "HKD", "ZAR", "SEK", "NOK", "DKK",
    };

    private static readonly Dictionary<string, string> Symbols = new(StringComparer.Ordinal)
    {
        ["€"] = "EUR",
        ["£"] = "GBP",
        ["₹"] = "INR",
        ["$"] = "$",
        ["¥"] = "¥",
    };

    /// <summary>Currency stated by a number format such as "$#,##0.00", "[$€-x-euro2] #,##0" or "#,##0 \"AED\"".</summary>
    public static string? FromNumberFormat(string? numberFormat)
    {
        if (string.IsNullOrWhiteSpace(numberFormat))
        {
            return null;
        }

        // Locale currency blocks: [$€-x-euro2], [$USD], [$-409] (the last has no currency).
        foreach (Match match in LocaleCurrencyBlock().Matches(numberFormat))
        {
            var symbol = match.Groups["symbol"].Value.Trim();
            if (Normalize(symbol) is { } currency)
            {
                return currency;
            }
        }

        // Quoted literals: "AED" #,##0.00
        foreach (Match match in QuotedLiteral().Matches(numberFormat))
        {
            if (FromText(match.Groups["text"].Value) is { } currency)
            {
                return currency;
            }
        }

        // Unquoted symbols outside [...] blocks and quotes, e.g. $#,##0.00 or #,##0.00 €.
        var unquoted = QuotedLiteral().Replace(BracketBlock().Replace(numberFormat, string.Empty), string.Empty);
        return Symbols.Keys.FirstOrDefault(symbol => unquoted.Contains(symbol, StringComparison.Ordinal)) is { } found
            ? Symbols[found]
            : null;
    }

    /// <summary>Currency stated in free text such as a header "Amount (USD)" or "Rate €".</summary>
    public static string? FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (Match match in UppercaseWord().Matches(text))
        {
            if (IsoCodes.Contains(match.Value))
            {
                return match.Value;
            }
        }

        return Symbols.Keys.FirstOrDefault(symbol => text.Contains(symbol, StringComparison.Ordinal)) is { } found
            ? Symbols[found]
            : null;
    }

    private static string? Normalize(string symbolOrCode)
    {
        if (IsoCodes.Contains(symbolOrCode))
        {
            return symbolOrCode;
        }

        return Symbols.TryGetValue(symbolOrCode, out var currency) ? currency : null;
    }

    [GeneratedRegex(@"\[\$(?<symbol>[^\]\-]*)(-[^\]]*)?\]")]
    private static partial Regex LocaleCurrencyBlock();

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex BracketBlock();

    [GeneratedRegex("\"(?<text>[^\"]*)\"")]
    private static partial Regex QuotedLiteral();

    [GeneratedRegex(@"\b[A-Z]{3}\b")]
    private static partial Regex UppercaseWord();
}
