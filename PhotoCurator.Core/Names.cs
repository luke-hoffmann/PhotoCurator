using System.Globalization;
using System.Text;

namespace PhotoCurator.Core;

/// <summary>Folder-name rules, URL slugs and the natural sort order used everywhere in the UI.</summary>
public static class Names
{
    static readonly char[] InvalidChars = "<>:\"/\\|?*".ToCharArray();
    static readonly HashSet<string> Reserved = new(
        new[] { "CON", "PRN", "AUX", "NUL" }.Concat(Enumerable.Range(1, 9).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" })),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the trimmed folder name, or throws with a message that says what to change. The rules are Windows'
    /// (the strictest), applied on every OS so a library made anywhere opens on Windows.
    /// </summary>
    public static string ValidateFolderName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) throw new CuratorException("Give the folder a name.");
        if (trimmed.Length > 120) throw new CuratorException("That name is too long (120 characters at most).");
        if (trimmed is "." or "..") throw new CuratorException("That name isn't allowed.");
        if (trimmed.StartsWith('.')) throw new CuratorException("Folder names can't start with a dot (those folders are hidden).");
        if (trimmed.IndexOfAny(InvalidChars) >= 0 || trimmed.Any(char.IsControl))
        {
            throw new CuratorException("Folder names can't contain < > : \" / \\ | ? *");
        }
        if (trimmed.EndsWith('.')) throw new CuratorException("Folder names can't end with a dot.");
        var stem = trimmed.Split('.')[0].Trim();
        if (Reserved.Contains(stem)) throw new CuratorException($"\"{trimmed}\" is a reserved name on Windows.");
        return trimmed;
    }

    /// <summary>Lower-case, dash-separated, ASCII: "Prepping for Paint (Lynden)" → "prepping-for-paint-lynden".</summary>
    public static string Slug(string text, int maxLength = 80)
    {
        string decomposed;
        try { decomposed = text.Normalize(NormalizationForm.FormD); }
        catch (PlatformNotSupportedException) { decomposed = text; }

        var sb = new StringBuilder(decomposed.Length);
        var dash = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            var lower = char.ToLowerInvariant(c);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(lower);
                dash = false;
            }
            else if (!dash && sb.Length > 0)
            {
                sb.Append('-');
                dash = true;
            }
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > maxLength) slug = slug[..maxLength].TrimEnd('-');
        return slug.Length == 0 ? "untitled" : slug;
    }

    /// <summary>Normalises a tag the way it is stored: trimmed, single spaces, lower case.</summary>
    public static string? NormalizeTag(string? tag)
    {
        if (tag is null) return null;
        var parts = tag.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var joined = string.Join(' ', parts).ToLowerInvariant();
        if (joined.Length == 0) return null;
        return joined.Length > 60 ? joined[..60].TrimEnd() : joined;
    }

    /// <summary>"IMG_2" sorts before "IMG_10"; case-insensitive.</summary>
    public static readonly IComparer<string> Natural = new NaturalComparer();

    sealed class NaturalComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (si < x.Length && x[si] == '0') si++;
                    while (sj < y.Length && y[sj] == '0') sj++;
                    int ei = si, ej = sj;
                    while (ei < x.Length && char.IsAsciiDigit(x[ei])) ei++;
                    while (ej < y.Length && char.IsAsciiDigit(y[ej])) ej++;
                    var lengthDiff = (ei - si) - (ej - sj);
                    if (lengthDiff != 0) return lengthDiff;
                    var digits = string.CompareOrdinal(x, si, y, sj, ei - si);
                    if (digits != 0) return digits;
                    i = ei;
                    j = ej;
                    continue;
                }
                var a = char.ToLowerInvariant(x[i]);
                var b = char.ToLowerInvariant(y[j]);
                if (a != b) return a.CompareTo(b);
                i++;
                j++;
            }
            var rest = (x.Length - i) - (y.Length - j);
            return rest != 0 ? rest : string.CompareOrdinal(x, y);
        }
    }
}
