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
        "MXN",
    };

    private static readonly Dictionary<string, string> Symbols = new(StringComparer.Ordinal)
    {
        ["€"] = "EUR",
        ["£"] = "GBP",
        ["₹"] = "INR",
        ["$"] = "$",
        ["¥"] = "¥",
    };

    /// <summary>ISO codes an ambiguous symbol can stand for. A symbol is consistent with any code in its family.</summary>
    private static readonly Dictionary<string, HashSet<string>> SymbolFamilies = new(StringComparer.Ordinal)
    {
        ["$"] = new(StringComparer.Ordinal) { "USD", "CAD", "AUD", "NZD", "SGD", "HKD", "MXN" },
        ["¥"] = new(StringComparer.Ordinal) { "JPY", "CNY" },
    };

    /// <summary>
    /// Combines the currencies stated by a column's number formats and its header into one currency.
    /// An ambiguous symbol consistent with an ISO code resolves to that code ("$" with a USD header is USD);
    /// a bare symbol with nothing more specific stays a symbol. Genuinely different currencies are a conflict.
    /// </summary>
    public static CurrencyResolution Resolve(IEnumerable<string> formatCurrencies, string? headerCurrency)
    {
        string? fromFormats = null;
        foreach (var currency in formatCurrencies.Distinct(StringComparer.Ordinal))
        {
            if (!TryCombine(fromFormats, currency, out fromFormats))
            {
                return new CurrencyResolution(null, CurrencyConflict.BetweenFormats);
            }
        }

        return TryCombine(fromFormats, headerCurrency, out var combined)
            ? new CurrencyResolution(combined, CurrencyConflict.None)
            : new CurrencyResolution(null, CurrencyConflict.HeaderAndFormats);
    }

    private static bool TryCombine(string? first, string? second, out string? combined)
    {
        combined = first ?? second;
        if (first is null || second is null || first == second)
        {
            return true;
        }

        var firstIsCode = IsoCodes.Contains(first);
        var secondIsCode = IsoCodes.Contains(second);
        if (firstIsCode != secondIsCode)
        {
            var (code, symbol) = firstIsCode ? (first, second) : (second, first);
            combined = code;
            return SymbolFamilies.TryGetValue(symbol, out var family) && family.Contains(code);
        }

        // Two different ISO codes, or two different ambiguous symbols.
        combined = null;
        return false;
    }

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

public enum CurrencyConflict
{
    None,

    /// <summary>The cells are formatted in different currencies.</summary>
    BetweenFormats,

    /// <summary>The cells agree with each other but not with the header.</summary>
    HeaderAndFormats,
}

/// <summary>The resolved currency (null when none is stated or there is a conflict).</summary>
public sealed record CurrencyResolution(string? Currency, CurrencyConflict Conflict);
