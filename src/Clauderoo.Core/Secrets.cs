namespace Clauderoo.Core;

/// <summary>Helpers that keep secrets out of text shown to the user, logged or stored.</summary>
public static class Secrets
{
    /// <summary>Replaces every occurrence of <paramref name="secret"/> in <paramref name="text"/> with "***".</summary>
    public static string Scrub(string text, string? secret) =>
        string.IsNullOrEmpty(secret) ? text : text.Replace(secret, "***");
}

