using System;
using System.Linq;

namespace IzzysFurniture;

internal static class TextSearch
{
    public static bool ContainsAny(string text, params string[] values)
        => values.Any(text.Contains);

    public static bool ContainsAnyToken(string text, params string[] tokens)
    {
        var padded = $" {text} ";
        return tokens.Any(token => token.Length <= 3
            ? padded.Contains($" {token} ", StringComparison.OrdinalIgnoreCase)
            : text.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
}
