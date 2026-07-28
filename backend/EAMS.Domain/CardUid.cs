namespace EAMS.Domain;

/// <summary>
/// Card UIDs arrive from readers in many shapes (<c>04:a7:b8:c9</c>, <c>04-A7-B8-C9</c>,
/// <c>04 a7 b8 c9</c>). CLAUDE.md's rule: normalize first, compare second. Persisted UIDs are
/// always the normalized form, so every lookup must normalize its input.
/// </summary>
public static class CardUid
{
    public static string Normalize(string uid) =>
        new string(uid.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
}
