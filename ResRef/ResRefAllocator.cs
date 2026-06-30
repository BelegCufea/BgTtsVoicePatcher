namespace BgTtsVoicePatcher.ResRef;

/// <summary>
/// Deterministically derives an 8-character resref from a StrRef number, so the same
/// StrRef always maps to the same resref across runs without needing a lookup table.
/// Format: 2-character prefix + 6-character base36 number, e.g. prefix "TS" + StrRef
/// 12345 -> "TS0009IX".
///
/// Caveat: this only guarantees no collisions between StrRefs processed by this tool.
/// It does not cross-check against every resref already used elsewhere in the game's
/// BIFFs/override folder. Picking a distinctive prefix you're not likely to see
/// elsewhere (avoid single recognisable words) keeps the practical collision risk
/// effectively at zero, but if you want to be certain, grep your override folder and
/// extracted BIFFs for the prefix before a large run.
/// </summary>
public static class ResRefAllocator
{
    private const string Base36Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    // No upper-bound check needed: 36^6 - 1 (2,176,782,335) already exceeds
    // int.MaxValue, so every non-negative int fits in 6 base36 digits.

    public static string ForStrRef(int strRef, string prefix)
    {
        if (prefix.Length != 2)
            throw new ArgumentException("Prefix must be exactly 2 characters.", nameof(prefix));

        if (strRef < 0)
            throw new ArgumentOutOfRangeException(nameof(strRef), "StrRef must be non-negative.");

        var suffix = ToBase36(strRef).PadLeft(6, '0');
        return (prefix + suffix).ToUpperInvariant();
    }

    private static string ToBase36(int value)
    {
        if (value == 0)
            return "0";

        var chars = new Stack<char>();
        while (value > 0)
        {
            chars.Push(Base36Alphabet[value % 36]);
            value /= 36;
        }

        return new string(chars.ToArray());
    }
}
