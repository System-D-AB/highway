using System.Globalization;

namespace Highway.Server.Host.Configuration;

/// <summary>
/// Parses size strings the way operators write them: <c>"512m"</c>, <c>"1g"</c>,
/// <c>"32k"</c>, or plain bytes. Suffixes are powers of 1024. A plain number is bytes.
/// </summary>
internal static class SizeFormat
{
    public static long Parse(string? text, string context)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ConfigurationException($"{context}: no size given.");

        var span = text.AsSpan().Trim();
        var suffixStart = span.Length;
        while (suffixStart > 0 && !char.IsDigit(span[suffixStart - 1]))
            suffixStart--;

        var numberPart = span[..suffixStart].TrimEnd();
        var suffix = span[suffixStart..].Trim().ToString().ToLowerInvariant();

        if (numberPart.IsEmpty || !long.TryParse(numberPart, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            throw new ConfigurationException(
                $"{context}: '{text}' is not a size. Expected a number of bytes, optionally followed by k, m or g (e.g. \"512m\", \"1g\").");

        var multiplier = suffix switch
        {
            "" or "b" => 1L,
            "k" or "kb" => 1024L,
            "m" or "mb" => 1024L * 1024,
            "g" or "gb" => 1024L * 1024 * 1024,
            _ => throw new ConfigurationException(
                $"{context}: '{text}' has an unknown size suffix '{suffix}'. Use k, m or g.")
        };

        if (value < 0)
            throw new ConfigurationException($"{context}: '{text}' is negative.");

        return value * multiplier;
    }
}
