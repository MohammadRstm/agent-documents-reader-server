using System.Globalization;
using System.Text.RegularExpressions;

namespace DocumentReader.Api.Analysis;

/// <summary>
/// A date mention found in text. <see cref="Date"/> is null when the text is ambiguous (for example
/// 03/04/2027 could be 3 April or 4 March); <see cref="Candidates"/> then lists every reading.
/// </summary>
public sealed record FoundDate(DateOnly? Date, IReadOnlyList<DateOnly> Candidates, string MatchedText, int Index)
{
    public bool IsAmbiguous => Date is null;
}

/// <summary>
/// Finds explicit calendar dates in text with fixed patterns: ISO (2027-03-15), numeric
/// (15/03/2027, 15.03.2027) and written (15 March 2027, March 15, 2027). Relative or partial dates
/// ("next month", "Q2 2027") are not interpreted.
/// </summary>
public static partial class DateFinder
{
    private const int MinYear = 1900;
    private const int MaxYear = 2199;

    private static readonly string[] MonthNames =
        ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    public static IReadOnlyList<FoundDate> Find(string text)
    {
        var found = new List<FoundDate>();

        foreach (Match match in IsoDate().Matches(text))
        {
            AddIfValid(found, match, Year(match, "year"), Number(match, "month"), Number(match, "day"));
        }

        foreach (Match match in NumericDate().Matches(text))
        {
            var first = Number(match, "first");
            var second = Number(match, "second");
            var year = Year(match, "year");
            var dayFirst = TryCreate(year, second, first);
            var monthFirst = TryCreate(year, first, second);
            if (dayFirst is { } a && monthFirst is { } b && a != b)
            {
                found.Add(new FoundDate(null, [a, b], match.Value, match.Index));
            }
            else if ((dayFirst ?? monthFirst) is { } single)
            {
                found.Add(new FoundDate(single, [single], match.Value, match.Index));
            }
        }

        foreach (Match match in DayMonthYear().Matches(text))
        {
            AddIfValid(found, match, Year(match, "year"), Month(match), Number(match, "day"));
        }

        foreach (Match match in MonthDayYear().Matches(text))
        {
            AddIfValid(found, match, Year(match, "year"), Month(match), Number(match, "day"));
        }

        // Keep the first of any overlapping matches, in text order.
        var result = new List<FoundDate>();
        var coveredUntil = -1;
        foreach (var date in found.OrderBy(date => date.Index).ThenByDescending(date => date.MatchedText.Length))
        {
            if (date.Index >= coveredUntil)
            {
                result.Add(date);
                coveredUntil = date.Index + date.MatchedText.Length;
            }
        }

        return result;
    }

    private static void AddIfValid(List<FoundDate> found, Match match, int year, int month, int day)
    {
        if (TryCreate(year, month, day) is { } date)
        {
            found.Add(new FoundDate(date, [date], match.Value, match.Index));
        }
    }

    private static DateOnly? TryCreate(int year, int month, int day)
    {
        if (year is < MinYear or > MaxYear || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }

        return new DateOnly(year, month, day);
    }

    private static int Number(Match match, string group) =>
        int.Parse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture);

    private static int Year(Match match, string group) => Number(match, group);

    private static int Month(Match match) =>
        Array.IndexOf(MonthNames, match.Groups["month"].Value[..3].ToLowerInvariant()) + 1;

    private const string MonthPattern =
        "(?<month>jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)";

    [GeneratedRegex(@"(?<![\d-])(?<year>\d{4})-(?<month>\d{1,2})-(?<day>\d{1,2})(?![\d])")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"(?<![\d./-])(?<first>\d{1,2})[/.\-](?<second>\d{1,2})[/.\-](?<year>\d{4})(?!\d)")]
    private static partial Regex NumericDate();

    [GeneratedRegex(@"\b(?<day>\d{1,2})(?:st|nd|rd|th)?\s+(?:of\s+)?" + MonthPattern + @"\.?,?\s+(?<year>\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex DayMonthYear();

    [GeneratedRegex(@"\b" + MonthPattern + @"\.?\s+(?<day>\d{1,2})(?:st|nd|rd|th)?,?\s+(?<year>\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex MonthDayYear();
}
