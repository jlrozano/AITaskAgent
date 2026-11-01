using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AITaskAgent.Script.Worker;

/// <summary>
/// The small standard library the worker gives scripts as .NET delegates: pure functions, no I/O, no state. They exist
/// because V8 has no hashing, base64 of UTF-8 text, GUIDs or time zones. All of them are culture-invariant.
/// </summary>
internal static class NativeLibrary
{
    /// <summary>Hex (lower case) digest of the UTF-8 text; <paramref name="algorithm"/> is <c>sha256</c>, <c>sha1</c> or <c>md5</c>.</summary>
    public static string Hash(string algorithm, string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        byte[] digest = algorithm switch
        {
            "sha256" => SHA256.HashData(data),
            "sha1" => SHA1.HashData(data),
            "md5" => MD5.HashData(data),
            _ => throw new ArgumentException($"unknown hash algorithm '{algorithm}'."),
        };
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>Base64 of the UTF-8 bytes of the text.</summary>
    public static string Base64Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    /// <summary>The UTF-8 text inside a base64 string; an invalid string or invalid UTF-8 is an error.</summary>
    public static string Base64Decode(string base64)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            throw new ArgumentException("not a valid base64 string.");
        }
        catch (DecoderFallbackException)
        {
            throw new ArgumentException("the base64 string does not hold valid UTF-8 text.");
        }
    }

    /// <summary>A random version 4 GUID, lower case, with hyphens.</summary>
    public static string Uuid() => Guid.NewGuid().ToString("D");

    /// <summary>
    /// Formats a moment (ISO 8601 text, or milliseconds since the Unix epoch as text) with a .NET custom format string,
    /// in <paramref name="zone"/> (<c>UTC</c>, a fixed offset such as <c>+02:00</c>, or a system time zone id).
    /// </summary>
    public static string DateFormat(string value, string format, string? zone)
    {
        DateTimeOffset moment = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        return InZone(moment, zone).ToString(string.IsNullOrEmpty(format) ? "o" : format, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Parses text with an exact .NET custom format (empty = ISO 8601) read as local time of <paramref name="zone"/>, and returns
    /// the moment as UTC ISO 8601 (<c>yyyy-MM-ddTHH:mm:ss.fffZ</c>).
    /// </summary>
    public static string DateParse(string text, string format, string? zone)
    {
        TimeSpan offset = OffsetOf(zone, null);
        DateTimeOffset moment;
        if (string.IsNullOrEmpty(format))
        {
            moment = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        }
        else
        {
            DateTime local = DateTime.ParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None);
            TimeSpan resolved = TimeZoneOrNull(zone) is { } tz ? tz.GetUtcOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified)) : offset;
            moment = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), resolved);
        }

        return moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset InZone(DateTimeOffset moment, string? zone)
    {
        if (TimeZoneOrNull(zone) is { } tz)
        {
            return TimeZoneInfo.ConvertTime(moment, tz);
        }

        return moment.ToOffset(OffsetOf(zone, moment));
    }

    private static TimeSpan OffsetOf(string? zone, DateTimeOffset? at)
    {
        if (string.IsNullOrEmpty(zone) || zone.Equals("UTC", StringComparison.OrdinalIgnoreCase) || zone == "Z")
        {
            return TimeSpan.Zero;
        }

        if ((zone[0] is '+' or '-') && TimeSpan.TryParse(zone.TrimStart('+'), CultureInfo.InvariantCulture, out TimeSpan fixedOffset))
        {
            return fixedOffset;
        }

        if (TimeZoneOrNull(zone) is { } tz)
        {
            return at is { } moment ? tz.GetUtcOffset(moment) : tz.BaseUtcOffset;
        }

        throw new ArgumentException($"unknown time zone '{zone}' (use UTC, an offset such as +02:00, or a system time zone id).");
    }

    private static TimeZoneInfo? TimeZoneOrNull(string? zone)
    {
        if (string.IsNullOrEmpty(zone) || zone.Equals("UTC", StringComparison.OrdinalIgnoreCase) || zone == "Z" || zone[0] is '+' or '-')
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(zone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException($"unknown time zone '{zone}' (use UTC, an offset such as +02:00, or a system time zone id).");
        }
    }
}
