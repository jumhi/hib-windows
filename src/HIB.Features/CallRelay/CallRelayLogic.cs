using System.Globalization;
using System.Text;

namespace HIB.Features.CallRelay;

/// <summary>
/// Validates what the user typed into a dial string that is safe to hand to the phone's shell.
/// Mirrors the Mac's <c>DialNumber</c> (PhoneRelayLogic.swift).
/// </summary>
public static class DialNumber
{
    public const int MaxLength = 40;
    const string Separators = " -–—.()/[]  ‎‏";

    /// <summary>Digits (any script → ASCII), one leading <c>+</c>, <c>*</c>, <c>#</c> and <c>,</c> pauses.
    /// Spaces, dashes, dots, slashes and brackets are dropped; anything else → null.</summary>
    public static string? Sanitize(string? raw)
    {
        if (raw is null) return null;
        var sb = new StringBuilder();
        foreach (var rune in raw.Trim().EnumerateRunes())
        {
            var v = rune.Value;
            if (v is >= '0' and <= '9') sb.Append((char)v);
            else if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber)
            {
                var d = (int)Rune.GetNumericValue(rune);
                if (d is < 0 or > 9) return null;
                sb.Append((char)('0' + d));
            }
            else if (v is '+' or '＋')
            {
                if (sb.Length != 0) return null;
                sb.Append('+');
            }
            else if (v is '*' or '#') sb.Append((char)v);
            else if (v == ',')
            {
                if (sb.Length == 0) return null;
                sb.Append(',');
            }
            else if ((v <= 0xFFFF && Separators.Contains((char)v)) || Rune.IsWhiteSpace(rune)) continue;
            else return null;
        }
        var s = sb.ToString();
        if (s.Length == 0 || s.Length > MaxLength) return null;
        if (!s.Any(c => c is >= '0' and <= '9' or '*' or '#')) return null;
        return s;
    }

    /// <summary><c>tel:</c> URI with <c>*</c>/<c>#</c> percent-encoded: only <c>[0-9+,%A-F]</c> after the scheme.</summary>
    public static string TelUri(string sanitized)
    {
        var sb = new StringBuilder("tel:");
        foreach (var c in sanitized)
            sb.Append(c switch { '*' => "%2A", '#' => "%23", _ => c.ToString() });
        return sb.ToString();
    }

    /// <summary><c>-s serial shell am start -a android.intent.action.CALL -d tel:…</c>, or null for a bad number.</summary>
    public static List<string>? CallArguments(string serial, string? rawNumber)
    {
        var n = Sanitize(rawNumber);
        if (n is null) return null;
        return ["-s", serial, "shell", "am", "start", "-a", "android.intent.action.CALL", "-d", TelUri(n)];
    }
}

/// <summary>TelephonyManager.CALL_STATE_*.</summary>
public enum PhoneCallState { Idle = 0, Ringing = 1, OffHook = 2 }

public static class PhoneCallStates
{
    public static readonly string[] DumpsysArguments = ["shell", "dumpsys", "telephony.registry"];

    /// <summary>Highest <c>mCallState=</c> across SIM slots, or null when none is reported.</summary>
    public static PhoneCallState? Parse(string? dumpsys)
    {
        int? best = null;
        foreach (var raw in (dumpsys ?? "").Split('\n'))
        {
            var t = raw.Trim();
            if (!t.StartsWith("mCallState=", StringComparison.Ordinal)) continue;
            var digits = new string(t["mCallState=".Length..].TakeWhile(char.IsAsciiDigit).ToArray());
            if (int.TryParse(digits, out var v)) best = Math.Max(best ?? v, v);
        }
        return best switch { null => null, 0 => PhoneCallState.Idle, 1 => PhoneCallState.Ringing, _ => PhoneCallState.OffHook };
    }
}

public enum CallKeyAction { Answer, Decline, End }

public static class CallKeys
{
    /// <summary>KEYCODE_CALL answers a ringing call; KEYCODE_ENDCALL rejects/hangs up. Only valid while
    /// ringing / in a call (otherwise CALL opens the dialer and ENDCALL turns the screen off).</summary>
    public static string Keycode(CallKeyAction a) => a == CallKeyAction.Answer ? "KEYCODE_CALL" : "KEYCODE_ENDCALL";

    /// <summary>The H.I.B protocol <c>callControl</c> action.</summary>
    public static string ProtocolAction(CallKeyAction a) => a switch
    {
        CallKeyAction.Answer => "accept",
        CallKeyAction.Decline => "decline",
        _ => "end",
    };

    public static List<string> AdbArguments(CallKeyAction a, string serial) => ["-s", serial, "shell", "input", "keyevent", Keycode(a)];

    public static bool IsAllowed(CallKeyAction a, PhoneCallState state) => a switch
    {
        CallKeyAction.Answer or CallKeyAction.Decline => state == PhoneCallState.Ringing,
        _ => state != PhoneCallState.Idle,
    };
}

public sealed record PhoneContact(string Name, string Number)
{
    public string Id => Name + "|" + Number;
}

public static class ContactsQuery
{
    public static readonly string[] AdbArguments =
        ["shell", "content", "query", "--uri", "content://com.android.contacts/data/phones", "--projection", "display_name:data1"];

    /// <summary>Parses <c>Row: 0 display_name=Mom, data1=+966…</c>; the number follows the last <c>, data1=</c>.</summary>
    public static List<PhoneContact> Parse(string? output)
    {
        var seen = new HashSet<string>();
        var list = new List<PhoneContact>();
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var s = raw.TrimEnd('\r');
            if (!s.StartsWith("Row:", StringComparison.Ordinal)) continue;
            var ns = s.IndexOf("display_name=", StringComparison.Ordinal);
            var ds = s.LastIndexOf(", data1=", StringComparison.Ordinal);
            if (ns < 0 || ds < ns + "display_name=".Length) continue;
            var name = s[(ns + "display_name=".Length)..ds].Trim();
            var number = s[(ds + ", data1=".Length)..].Trim();
            if (number.Length == 0 || number == "NULL") continue;
            if (name.Length == 0 || name == "NULL") name = number;
            var key = name.ToLowerInvariant() + "|" + new string(number.Where(c => char.IsDigit(c) || c == '+').ToArray());
            if (seen.Add(key)) list.Add(new PhoneContact(name, number));
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    public static List<PhoneContact> Filter(IEnumerable<PhoneContact> contacts, string? query, int limit = 30)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return contacts.Take(limit).ToList();
        var digits = new string(q.Where(char.IsAsciiDigit).ToArray());
        var cmp = CultureInfo.CurrentCulture.CompareInfo;
        return contacts.Where(c =>
                cmp.IndexOf(c.Name, q, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0
                || (digits.Length >= 2 && new string(c.Number.Where(char.IsAsciiDigit).ToArray()).Contains(digits, StringComparison.Ordinal)))
            .Take(limit).ToList();
    }
}

public static class CallAudioArgs
{
    /// <summary>Tried in order: both directions, then the other party only.</summary>
    public static readonly string[] Sources = ["voice-call", "voice-call-downlink"];

    public static List<string> Build(string serial, string source) =>
    [
        "-s", serial,
        "--no-video",
        "--no-control",
        "--no-window",
        $"--audio-source={source}",
        "--audio-codec=opus",
        "--audio-buffer=60",
        "--require-audio",
    ];
}
