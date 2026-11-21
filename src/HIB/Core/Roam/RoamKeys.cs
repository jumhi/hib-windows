// H.I.B Roam (docs/SPACE.md §9.3): keys travel as physical keys — USB HID keyboard-page usages — so the
// receiving computer's own layout and input language apply. This table turns Windows set-1 scancodes
// (with the E0 "extended" prefix) into HID usages and back, maps modifiers between Mac and PC, and
// converts wheel notches. Media keys without a keyboard-page usage travel as HID extended usages
// (usage page 0x0C, consumer, in the high 16 bits: 0x000C00CD = play/pause).
using System;
using System.Collections.Generic;

namespace HIB.Core.Roam;

/// <summary>How a key is typed on Windows: a scancode (set 1, <see cref="Extended"/> = E0 prefix) or, for keys that
/// have no single scancode (Pause, NumLock, media), a virtual-key code.</summary>
public readonly record struct WinKey(ushort Scan, bool Extended, ushort Vk = 0)
{
    /// <summary>Inject this by virtual key (wVk) rather than by scancode.</summary>
    public bool ByVk => Vk != 0;
}

public static class RoamKeys
{
    public const int ConsumerPage = 0x0C;

    /// <summary>A consumer-page key as a HID extended usage (page in the high 16 bits).</summary>
    public static int Consumer(int id) => (ConsumerPage << 16) | id;

    // HID consumer usages for media keys.
    public static readonly int PlayPause = Consumer(0xCD);
    public static readonly int NextTrack = Consumer(0xB5);
    public static readonly int PrevTrack = Consumer(0xB6);
    public static readonly int StopMedia = Consumer(0xB7);
    public static readonly int ConsumerMute = Consumer(0xE2);
    public static readonly int ConsumerVolumeUp = Consumer(0xE9);
    public static readonly int ConsumerVolumeDown = Consumer(0xEA);
    public static readonly int BrightnessUp = Consumer(0x6F);
    public static readonly int BrightnessDown = Consumer(0x70);

    // Keyboard-page usages used by name.
    public const int Escape = 0x29;
    public const int LeftCtrl = 0xE0, LeftShift = 0xE1, LeftAlt = 0xE2, LeftGui = 0xE3;
    public const int RightCtrl = 0xE4, RightShift = 0xE5, RightAlt = 0xE6, RightGui = 0xE7;
    public const int Mute = 0x7F, VolumeUp = 0x80, VolumeDown = 0x81;
    public const int PrintScreen = 0x46, Pause = 0x48, NumLock = 0x53;

    // Windows virtual keys with no usable scancode of their own.
    const ushort VK_PAUSE = 0x13, VK_SNAPSHOT = 0x2C, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_NUMLOCK = 0x90;
    const ushort VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;
    const ushort VK_MEDIA_NEXT_TRACK = 0xB0, VK_MEDIA_PREV_TRACK = 0xB1, VK_MEDIA_STOP = 0xB2, VK_MEDIA_PLAY_PAUSE = 0xB3;

    /// <summary>HID usage → Windows key (set-1 scancode; 0x100 marks the E0 prefix in the table).</summary>
    static readonly Dictionary<int, WinKey> ToWin = new();
    /// <summary>(scancode | 0x100 for E0) → HID usage.</summary>
    static readonly Dictionary<int, int> FromScan = new();
    /// <summary>Virtual keys that decide the usage on their own (ambiguous or scancode-less keys).</summary>
    static readonly Dictionary<ushort, int> FromVk = new()
    {
        [VK_PAUSE] = Pause,
        [VK_NUMLOCK] = NumLock,
        [VK_SNAPSHOT] = PrintScreen,
        [VK_LWIN] = LeftGui,
        [VK_RWIN] = RightGui,
        [VK_VOLUME_MUTE] = Mute,
        [VK_VOLUME_DOWN] = VolumeDown,
        [VK_VOLUME_UP] = VolumeUp,
        [VK_MEDIA_NEXT_TRACK] = Consumer(0xB5),
        [VK_MEDIA_PREV_TRACK] = Consumer(0xB6),
        [VK_MEDIA_STOP] = Consumer(0xB7),
        [VK_MEDIA_PLAY_PAUSE] = Consumer(0xCD),
    };

    static void Add(int usage, int scan, bool reverse = true)
    {
        var key = new WinKey((ushort)(scan & 0xFF), (scan & 0x100) != 0);
        ToWin[usage] = key;
        if (reverse) FromScan[scan] = usage;
    }

    static void AddVk(int usage, ushort vk, int scan)
    {
        ToWin[usage] = new WinKey((ushort)(scan & 0xFF), (scan & 0x100) != 0, vk);
    }

    static RoamKeys()
    {
        const int E0 = 0x100;
        // Letters a–z.
        int[] letters = { 0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32, 0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C };
        for (var i = 0; i < letters.Length; i++) Add(0x04 + i, letters[i]);
        // 1 … 9, 0.
        for (var i = 0; i < 10; i++) Add(0x1E + i, 0x02 + i);
        Add(0x28, 0x1C);       // Enter
        Add(0x29, 0x01);       // Escape
        Add(0x2A, 0x0E);       // Backspace
        Add(0x2B, 0x0F);       // Tab
        Add(0x2C, 0x39);       // Space
        Add(0x2D, 0x0C);       // - _
        Add(0x2E, 0x0D);       // = +
        Add(0x2F, 0x1A);       // [ {
        Add(0x30, 0x1B);       // ] }
        Add(0x31, 0x2B);       // \ |
        Add(0x32, 0x2B, reverse: false); // non-US # ~ (the same key on ISO boards)
        Add(0x33, 0x27);       // ; :
        Add(0x34, 0x28);       // ' "
        Add(0x35, 0x29);       // ` ~
        Add(0x36, 0x33);       // , <
        Add(0x37, 0x34);       // . >
        Add(0x38, 0x35);       // / ?
        Add(0x39, 0x3A);       // Caps Lock
        for (var i = 0; i < 10; i++) Add(0x3A + i, 0x3B + i); // F1–F10
        Add(0x44, 0x57);       // F11
        Add(0x45, 0x58);       // F12
        Add(PrintScreen, E0 | 0x37);
        Add(0x47, 0x46);       // Scroll Lock
        AddVk(Pause, VK_PAUSE, 0x45);
        Add(0x49, E0 | 0x52);  // Insert
        Add(0x4A, E0 | 0x47);  // Home
        Add(0x4B, E0 | 0x49);  // Page Up
        Add(0x4C, E0 | 0x53);  // Delete
        Add(0x4D, E0 | 0x4F);  // End
        Add(0x4E, E0 | 0x51);  // Page Down
        Add(0x4F, E0 | 0x4D);  // Right
        Add(0x50, E0 | 0x4B);  // Left
        Add(0x51, E0 | 0x50);  // Down
        Add(0x52, E0 | 0x48);  // Up
        AddVk(NumLock, VK_NUMLOCK, E0 | 0x45);
        Add(0x54, E0 | 0x35);  // keypad /
        Add(0x55, 0x37);       // keypad *
        Add(0x56, 0x4A);       // keypad -
        Add(0x57, 0x4E);       // keypad +
        Add(0x58, E0 | 0x1C);  // keypad Enter
        int[] keypad = { 0x4F, 0x50, 0x51, 0x4B, 0x4C, 0x4D, 0x47, 0x48, 0x49, 0x52 }; // 1–9, 0
        for (var i = 0; i < keypad.Length; i++) Add(0x59 + i, keypad[i]);
        Add(0x63, 0x53);       // keypad .
        Add(0x64, 0x56);       // non-US \ | (ISO key left of Z)
        Add(0x65, E0 | 0x5D);  // Application (menu)
        Add(0x67, 0x59);       // keypad =
        int[] f13 = { 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x76 }; // F13–F24
        for (var i = 0; i < f13.Length; i++) Add(0x68 + i, f13[i]);
        Add(0x85, 0x7E);       // keypad , (Brazil)
        Add(0x87, 0x73);       // International1 (Ro)
        Add(0x88, 0x70);       // International2 (Katakana/Hiragana)
        Add(0x89, 0x7D);       // International3 (Yen)
        Add(0x8A, 0x79);       // International4 (Henkan)
        Add(0x8B, 0x7B);       // International5 (Muhenkan)
        Add(0x90, 0x72);       // LANG1 (Hangul / Kana)
        Add(0x91, 0x71);       // LANG2 (Hanja / Eisu)
        // Modifiers.
        Add(LeftCtrl, 0x1D);
        Add(LeftShift, 0x2A);
        Add(LeftAlt, 0x38);
        Add(LeftGui, E0 | 0x5B);
        Add(RightCtrl, E0 | 0x1D);
        Add(RightShift, 0x36);
        Add(RightAlt, E0 | 0x38);
        Add(RightGui, E0 | 0x5C);
        // Volume (keyboard page and consumer page both land on the Windows volume keys).
        AddVk(Mute, VK_VOLUME_MUTE, E0 | 0x20);
        AddVk(VolumeUp, VK_VOLUME_UP, E0 | 0x30);
        AddVk(VolumeDown, VK_VOLUME_DOWN, E0 | 0x2E);
        AddVk(ConsumerMute, VK_VOLUME_MUTE, E0 | 0x20);
        AddVk(ConsumerVolumeUp, VK_VOLUME_UP, E0 | 0x30);
        AddVk(ConsumerVolumeDown, VK_VOLUME_DOWN, E0 | 0x2E);
        AddVk(PlayPause, VK_MEDIA_PLAY_PAUSE, E0 | 0x22);
        AddVk(NextTrack, VK_MEDIA_NEXT_TRACK, E0 | 0x19);
        AddVk(PrevTrack, VK_MEDIA_PREV_TRACK, E0 | 0x10);
        AddVk(StopMedia, VK_MEDIA_STOP, E0 | 0x24);
        // The media scancodes also come back as their usages when a keyboard reports no virtual key.
        FromScan[E0 | 0x20] = Mute;
        FromScan[E0 | 0x30] = VolumeUp;
        FromScan[E0 | 0x2E] = VolumeDown;
        FromScan[E0 | 0x22] = PlayPause;
        FromScan[E0 | 0x19] = NextTrack;
        FromScan[E0 | 0x10] = PrevTrack;
        FromScan[E0 | 0x24] = StopMedia;
    }

    /// <summary>Every usage this table can type on Windows.</summary>
    public static IEnumerable<int> KnownUsages => ToWin.Keys;

    /// <summary>The Windows key for a HID usage, or null (unknown keys and brightness are dropped on Windows).</summary>
    public static WinKey? ToWindows(int usage) => ToWin.TryGetValue(usage, out var k) ? k : null;

    /// <summary>
    /// A key from Windows' low-level keyboard hook → HID usage, or null. <paramref name="scan"/> is the hook's scanCode,
    /// <paramref name="extended"/> its LLKHF_EXTENDED flag and <paramref name="vk"/> its vkCode. The fake Left Ctrl that
    /// AltGr layouts send (scancode with bit 0x200) returns null: the receiving side makes its own.
    /// </summary>
    public static int? FromWindows(int scan, bool extended, int vk)
    {
        if ((scan & 0x200) != 0) return null;
        if (FromVk.TryGetValue((ushort)vk, out var byVk)) return byVk;
        var key = (scan & 0xFF) | (extended ? 0x100 : 0);
        return FromScan.TryGetValue(key, out var usage) ? usage : null;
    }

    /// <summary>True for Control/Shift/Alt/GUI (left or right).</summary>
    public static bool IsModifier(int usage) => usage is >= 0xE0 and <= 0xE7;

    /// <summary>
    /// SPACE.md §9.3 modifier mapping, done by the controller (it knows the target's platform). With
    /// <paramref name="cmdAsCtrl"/> (the default) between a Mac and a PC, ⌘ ↔ Ctrl and Control ↔ Windows key swap places
    /// (Mac ⌘ → Ctrl, Mac Control → Win; PC Ctrl → ⌘, Win → Control); Option ↔ Alt stays. Off, or between two computers
    /// of the same kind, keys pass unchanged (⌘ ↔ Win, Control ↔ Ctrl).
    /// </summary>
    public static int MapModifier(int usage, string fromPlatform, string toPlatform, bool cmdAsCtrl)
    {
        if (!cmdAsCtrl) return usage;
        var from = Norm(fromPlatform);
        var to = Norm(toPlatform);
        if (from == to) return usage;
        return usage switch
        {
            LeftGui => LeftCtrl,
            LeftCtrl => LeftGui,
            RightGui => RightCtrl,
            RightCtrl => RightGui,
            _ => usage,
        };
    }

    static string Norm(string? platform) => (platform ?? "").Trim().ToLowerInvariant() == "windows" ? "windows" : "mac";
}

/// <summary>SPACE.md §9.3 ctlWheel: 120 = one notch; dy &gt; 0 scrolls up/away, dx &gt; 0 scrolls right.</summary>
public static class RoamWheel
{
    public const int Notch = 120;

    /// <summary>Windows' low-level hook mouseData (the signed high word) → ctlWheel. WM_MOUSEWHEEL forward = up; WM_MOUSEHWHEEL positive = right.</summary>
    public static (int Dx, int Dy) FromWindows(int mouseDataHighWord, bool horizontal)
        => horizontal ? (mouseDataHighWord, 0) : (0, mouseDataHighWord);

    /// <summary>The high word of MSLLHOOKSTRUCT.mouseData as a signed wheel delta.</summary>
    public static int HighWord(uint mouseData) => unchecked((short)(mouseData >> 16));

    /// <summary>ctlWheel → SendInput mouseData values (WHEEL for dy, HWHEEL for dx), clamped to what one event carries.</summary>
    public static (int Vertical, int Horizontal) ToWindows(double dx, double dy)
    {
        static int Clamp(double v) => double.IsFinite(v) ? (int)Math.Clamp(Math.Round(v), -Notch * 100, Notch * 100) : 0;
        return (Clamp(dy), Clamp(dx));
    }
}
