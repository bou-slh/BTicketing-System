namespace RapidsolDestek.Domain.Services;

/// <summary>
/// Formats sequence values through osTicket-style number formats: every run of '#'
/// is replaced by the value padded to the run's length with the sequence's padding
/// character ("R######" + 716555 + '0' → "R716555"; overflow keeps all digits).
/// </summary>
public static class TicketNumberFormatter
{
    public static string Format(string format, long value, char padding = '0')
    {
        if (string.IsNullOrEmpty(format))
            return value.ToString();

        var digits = value.ToString();
        var result = new System.Text.StringBuilder(format.Length + digits.Length);

        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '#')
            {
                result.Append(format[i]);
                continue;
            }

            var runLength = 0;
            while (i + runLength < format.Length && format[i + runLength] == '#')
                runLength++;
            i += runLength - 1;

            result.Append(digits.Length >= runLength
                ? digits
                : digits.PadLeft(runLength, padding));
        }

        return result.ToString();
    }
}
