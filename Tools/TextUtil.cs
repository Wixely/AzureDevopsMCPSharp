using System.Diagnostics.CodeAnalysis;

namespace AzureDevopsMCPSharp.Tools;

internal static class TextUtil
{
    /// <summary>
    /// Defends against callers (often LLMs) that send escaped newline sequences as the literal
    /// characters "\\n" / "\\r\\n" instead of real line breaks — which then render as a visible
    /// "\n" in descriptions and comments. Only rewrites when the text contains no real line break,
    /// so genuinely multi-line input (including code snippets that legitimately contain "\n") is
    /// left untouched.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? NormalizeNewlines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (text.Contains('\n') || text.Contains('\r')) return text;          // already has real breaks — trust it
        if (!text.Contains("\\n") && !text.Contains("\\r")) return text;      // nothing to fix
        return text.Replace("\\r\\n", "\n").Replace("\\r", "\n").Replace("\\n", "\n");
    }
}
