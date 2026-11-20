using System.Text;
using System.Text.RegularExpressions;

namespace HIB.Features.LoginCodes;

/// <summary>
/// Pure detector for one-time login codes (OTP / 2FA / verification codes) in notification text.
/// English + Arabic keyword-proximity scorer; regexes ported verbatim from OneTimeCodeDetector.swift.
/// String indices are UTF-16 code units, exactly like NSString.
/// </summary>
public static class OneTimeCodeDetector
{
    public sealed record Result(string Code);

    /// <summary>Most likely one-time code in title + body, or null.</summary>
    public static Result? Detect(string title, string body) => Detect(title + "\n" + body);

    public static Result? Detect(string rawText)
    {
        var text = Normalize(rawText);
        if (text.Length == 0) return null;
        var lower = text.ToLowerInvariant();

        var keywordRanges = KeywordRegex.Matches(text).Select(m => (m.Index, m.Length)).ToList();
        if (keywordRanges.Count == 0) return null;

        // Marketing codes ("promo code SAVE20") are not login codes unless a strong keyword is present.
        if (MarketingRegex.IsMatch(lower) && !StrongKeywordRegex.IsMatch(text)) return null;

        var candidates = new List<(string Code, int Loc, int Len, int Penalty)>();

        // 1. Digit runs, possibly split by a single space or hyphen ("123 456", "123-456").
        foreach (Match m in DigitGroupRegex.Matches(text))
        {
            var raw = m.Value;
            var groups = raw.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries);
            var totalDigits = groups.Sum(g => g.Length);

            if (!BoundaryOk(text, m.Index, m.Length)) continue;
            if (HasRejectingContext(text, m.Index, m.Length)) continue;

            if (groups.Length == 1)
            {
                var g = groups[0];
                if (g.Length is < 4 or > 8) continue;
                candidates.Add((g, m.Index, m.Length, Penalty(g)));
                continue;
            }

            // Phone numbers, card numbers, long references.
            if (totalDigits >= 9) continue;
            var usesHyphen = raw.Contains('-');

            if (groups.Length == 2 &&
                ((groups[0].Length == 3 && groups[1].Length == 3) || (groups[0].Length == 4 && groups[1].Length == 4)))
            {
                var joined = string.Concat(groups);
                candidates.Add((joined, m.Index, m.Length, Penalty(joined)));
                continue;
            }

            // Hyphenated numbers that aren't a 3-3/4-4 split are dates / references.
            if (usesHyphen || groups.Length >= 3) continue;

            // "482113 10 minutes": treat space-separated groups individually.
            var offset = 0;
            foreach (var g in groups)
            {
                var local = raw.IndexOf(g, offset, StringComparison.Ordinal);
                offset = local + g.Length;
                if (g.Length is < 4 or > 8) continue;
                candidates.Add((g, m.Index + local, g.Length, Penalty(g)));
            }
        }

        // 2. Alphanumeric codes directly after a keyword ("code: AB12CD").
        foreach (Match m in AlnumAfterKeywordRegex.Matches(text))
        {
            var group = m.Groups[1];
            if (!group.Success) continue;
            var code = group.Value;
            // Pure-digit tokens are handled above.
            if (code.All(char.IsDigit)) continue;
            candidates.Add((code, group.Index, group.Length, 0));
        }

        if (candidates.Count == 0) return null;

        // Score by distance to the nearest keyword; lower is better.
        const int maxDistance = 90;
        (string Code, int Score)? best = null;
        foreach (var c in candidates)
        {
            var minDistance = int.MaxValue;
            foreach (var (kLoc, kLen) in keywordRanges)
            {
                // Ignore keyword matches that overlap the candidate itself.
                if (Math.Max(kLoc, c.Loc) < Math.Min(kLoc + kLen, c.Loc + c.Len)) continue;
                var d = kLoc >= c.Loc + c.Len ? kLoc - (c.Loc + c.Len) : c.Loc - (kLoc + kLen);
                minDistance = Math.Min(minDistance, Math.Max(0, d));
            }
            if (minDistance > maxDistance) continue;
            var score = minDistance + c.Penalty;
            if (best is null || score < best.Value.Score) best = (c.Code, score);
        }
        return best is { } b ? new Result(b.Code) : null;
    }

    // MARK: - Helpers

    internal static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var rune in s.EnumerateRunes())
        {
            var v = rune.Value;
            if (v is >= 0x0660 and <= 0x0669) sb.Append((char)(v - 0x0660 + 0x30));       // Arabic-Indic digits
            else if (v is >= 0x06F0 and <= 0x06F9) sb.Append((char)(v - 0x06F0 + 0x30));  // Extended Arabic-Indic
            else if (v is 0x200E or 0x200F or 0x061C or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069) or 0xFEFF) continue; // bidi marks
            else if (v is 0x00A0 or 0x202F or 0x2007) sb.Append(' ');
            else if (v is >= 0x2010 and <= 0x2014) sb.Append('-');
            else sb.Append(rune.ToString());
        }
        return sb.ToString();
    }

    /// <summary>Prefer 6-digit codes; deprioritise year-looking numbers.</summary>
    private static int Penalty(string digits)
    {
        var p = digits.Length switch { 6 => 0, 4 or 8 => 4, _ => 6 };
        if (digits.Length == 4 && int.TryParse(digits, out var v) && v is >= 1950 and <= 2099) p += 30;
        return p;
    }

    /// <summary>The characters around the run must not make it part of an amount, time, date,
    /// phone number, masked card number or word.</summary>
    private static bool BoundaryOk(string s, int loc, int len)
    {
        char? At(int i) => i >= 0 && i < s.Length ? s[i] : null;
        var before = At(loc - 1);
        var before2 = At(loc - 2);
        var end = loc + len;
        var after = At(end);
        var after2 = At(end + 1);

        static bool IsLetter(char? c) => c is { } ch && char.IsLetter(ch);
        static bool IsDigit(char? c) => c is >= '0' and <= '9';

        if (IsLetter(before) || IsDigit(before)) return false;
        if (IsLetter(after) || IsDigit(after)) return false;
        if (before is { } b && "+#*$€£¥₹/@".Contains(b)) return false;
        if (after is { } a && "%$€£¥₹/@".Contains(a)) return false;
        // "1,250" / "12.50" / "12:30" — separator followed (or preceded) by a digit.
        if (after is { } a2 && ".,:".Contains(a2) && IsDigit(after2)) return false;
        if (before is { } b2 && ".,:".Contains(b2) && IsDigit(before2)) return false;
        return true;
    }

    /// <summary>Words just before / after the number that say it's an amount, account, order, etc.</summary>
    private static bool HasRejectingContext(string s, int loc, int len)
    {
        var beforeStart = Math.Max(0, loc - 24);
        var before = s[beforeStart..loc].ToLowerInvariant();
        var end = loc + len;
        var after = s.Substring(end, Math.Min(14, s.Length - end)).ToLowerInvariant();
        return RejectBeforeRegex.IsMatch(before) || RejectAfterRegex.IsMatch(after);
    }

    // MARK: - Patterns (verbatim from the Swift source; ICU and .NET agree on these constructs)

    private static readonly Regex KeywordRegex = new(
        @"(?i)\b(?:codes?|otp|verification|verify|passcode|pin|one[- ]?time|log[- ]?in|sign[- ]?in|2fa|mfa|two[- ]factor|security code|authenti[a-z]*|auth)\b|رمز|الرمز|كود|الكود|التحقق|تحقق|التفعيل|كلمة المرور|مرة واحدة",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex StrongKeywordRegex = new(
        @"(?i)\b(?:otp|verification|verify|passcode|one[- ]?time|log[- ]?in|sign[- ]?in|2fa|mfa|two[- ]factor|security code|authenti[a-z]*)\b|التحقق|تحقق|مرة واحدة",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MarketingRegex = new(
        @"promo|coupon|discount|voucher|referral|zip code|postal code|خصم|كوبون|قسيمة",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Maximal runs of digit groups joined by a single space or hyphen.</summary>
    private static readonly Regex DigitGroupRegex = new(@"\d+(?:[ -]\d+)*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>6–8 char uppercase alphanumeric with at least one letter and two digits right after a keyword.</summary>
    private static readonly Regex AlnumAfterKeywordRegex = new(
        @"(?:(?i:code|otp|passcode|pin)|رمز|كود)\s*(?:(?i:is)|:|=|-|هو)?\s*:?\s*((?=[A-Z0-9]*[A-Z])(?=[A-Z0-9]*\d[A-Z0-9]*\d)[A-Z0-9]{6,8})(?![A-Za-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RejectBeforeRegex = new(
        @"(?:order|invoice|ref(?:erence)?|tracking|account|acct|a/c|card|ending(?: in)?|amount|amt|balance|total|txn|transaction id|ticket|flight|booking|sar|usd|aed|egp|eur|gbp|kwd|qar|bhd|omr|inr|rs\.?|ريال|درهم|جنيه|دينار|مبلغ|بقيمة|رصيد|الطلب|طلب رقم|حساب|بطاقة|المنتهية)\W{0,4}(?:no\.?|number|#|:)?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RejectAfterRegex = new(
        @"^\s*(?:sar|usd|aed|egp|eur|gbp|kwd|qar|bhd|omr|inr|ريال|ر\.س|درهم|جنيه|دينار|dollars?|riyals?|%|percent|mb|gb|kb|kg|km)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
