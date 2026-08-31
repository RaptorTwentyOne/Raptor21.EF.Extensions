using System.Globalization;

namespace Raptor21.EF.Extensions.Migrations;

/// <summary>
/// Writes boundary values as T-SQL literals in one fixed spelling. The differ compares boundaries as text,
/// so a stable spelling is what keeps an unchanged boundary from reading as a removed one plus an added one.
/// </summary>
public static class PartitionBoundaries
{
    /// <summary>
    /// One boundary on the first day of each of <paramref name="count"/> consecutive months, starting with the
    /// month of <paramref name="firstMonth"/>. The day component of <paramref name="firstMonth"/> is ignored.
    /// With <see cref="PartitionRange.Right"/> each boundary opens its month.
    /// </summary>
    public static IReadOnlyList<string> Monthly(DateOnly firstMonth, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var month = new DateOnly(firstMonth.Year, firstMonth.Month, 1);
        var result = new string[count];
        for (var i = 0; i < count; i++)
            result[i] = Literal(month.AddMonths(i));

        return result;
    }

    /// <summary>
    /// Renders one value as a T-SQL literal: <c>DateTime</c> as <c>'yyyy-MM-ddTHH:mm:ss.fff'</c>, <c>DateOnly</c>
    /// as <c>'yyyyMMdd'</c>, integers and <c>decimal</c> invariantly, and a string as a single-quoted literal
    /// with embedded quotes doubled. The two date spellings are the ones every SQL Server date type reads as
    /// year-month-day under every <c>DATEFORMAT</c> and language setting — including legacy <c>datetime</c>
    /// and <c>smalldatetime</c>, which read the dash-separated spellings without <c>T</c> through the
    /// session's language and would swap day and month under a <c>dmy</c> login, such as a Turkish one.
    /// </summary>
    /// <exception cref="ArgumentException">The value's type has no literal form here.</exception>
    public static string Literal(object value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value switch
        {
            string s => $"'{s.Replace("'", "''")}'",
            DateOnly d => $"'{d.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}'",
            DateTime dt => $"'{dt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture)}'",
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            short sh => sh.ToString(CultureInfo.InvariantCulture),
            byte b => b.ToString(CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            _ => throw new ArgumentException(
                $"No T-SQL literal form for a boundary of type {value.GetType()}. Supported: string, DateOnly, " +
                "DateTime, int, long, short, byte, decimal. Write the literal yourself and pass it as a string " +
                "through the IEnumerable<string> overload.",
                nameof(value)),
        };
    }
}
