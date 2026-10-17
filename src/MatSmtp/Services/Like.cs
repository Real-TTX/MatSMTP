namespace MatSmtp.Services;

/// <summary>
/// The pattern for a "contains" search with LIKE: the characters that mean something to LIKE are taken literally (escaped with <see cref="Escape"/>).
/// SQLite compares ASCII letters without case; the search of a list is meant to be forgiving, so that is what it should do.
/// </summary>
public static class Like
{
    public const string Escape = "\\";

    public static string Contains(string text)
        => "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}
