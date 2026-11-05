using DIR.Lib;
using Microsoft.AspNetCore.Components.Web;

namespace WebGl.Renderer;

/// <summary>
/// Maps a browser keyboard event (<c>KeyboardEvent.key</c>, <c>KeyboardEvent.code</c> and the modifier
/// flags) to the platform-agnostic <see cref="InputKey"/> and <see cref="InputModifier"/> from DIR.Lib.
/// This is the browser analogue of SdlVulkan.Renderer's <c>SdlInputMapping</c>, and is meant to land on the
/// same <see cref="InputKey"/> that SDL does for the same physical key.
/// <para>
/// SDL maps by physical scancode; the browser's <c>key</c> is layout- and shift-dependent. So
/// <see cref="ToInputKey"/> tries <c>key</c> first (letters, named keys, unshifted symbols, and the
/// digits a keypad produces with NumLock on), then falls back to <c>code</c>, the physical key, so a
/// shifted or layout-changed key ("!" on Digit1, "_" on Minus, "=" on Equal) still lands where SDL lands.
/// </para>
/// <para>
/// Keypad decision: NumpadAdd is Plus, NumpadSubtract is Minus, NumpadDecimal is Period, NumpadDivide is
/// Slash and NumpadEnter is Enter, by <c>code</c>. Keypad digits give a digit <c>key</c> with NumLock on and
/// so reach D0..D9 through the key-first rule; with NumLock off <c>key</c> is ArrowUp, Home and so on, which
/// already map. Keypad digits are deliberately NOT mapped by <c>code</c> (NumLock-off Numpad5 is "Clear" and
/// must not become D5). NumpadMultiply has no <see cref="InputKey"/>, so it is None.
/// </para>
/// <para>
/// Differences from SDL: <c>[</c> and <c>]</c> (BracketLeft, BracketRight) are mapped here although
/// SdlInputMapping has no scancode for them. SDL's Android back button has no browser counterpart.
/// </para>
/// </summary>
public static class WebInputMapping
{
    /// <summary>Maps a browser key and physical code to an <see cref="InputKey"/>; <see cref="InputKey.None"/> if unknown.</summary>
    /// <param name="key"><c>KeyboardEvent.key</c>.</param>
    /// <param name="code"><c>KeyboardEvent.code</c>, or null when unavailable.</param>
    public static InputKey ToInputKey(string key, string? code)
    {
        var fromKey = FromKey(key);
        if (fromKey != InputKey.None) return fromKey;
        return code is null ? InputKey.None : FromCode(code);
    }

    /// <summary>Combines the browser's modifier flags into an <see cref="InputModifier"/>.</summary>
    public static InputModifier ToInputModifier(bool shift, bool ctrl, bool alt)
    {
        var mod = InputModifier.None;
        if (shift) mod |= InputModifier.Shift;
        if (ctrl) mod |= InputModifier.Ctrl;
        if (alt) mod |= InputModifier.Alt;
        return mod;
    }

    /// <summary>Maps a Blazor keyboard event to both its <see cref="InputKey"/> and its <see cref="InputModifier"/>.</summary>
    public static (InputKey Key, InputModifier Modifiers) ToInput(KeyboardEventArgs e)
        => (ToInputKey(e.Key, e.Code), ToInputModifier(e.ShiftKey, e.CtrlKey, e.AltKey));

    private static InputKey FromKey(string key) => key switch
    {
        "ArrowUp" => InputKey.Up,
        "ArrowDown" => InputKey.Down,
        "ArrowLeft" => InputKey.Left,
        "ArrowRight" => InputKey.Right,
        "Home" => InputKey.Home,
        "End" => InputKey.End,
        "PageUp" => InputKey.PageUp,
        "PageDown" => InputKey.PageDown,
        "Enter" => InputKey.Enter,
        "Escape" => InputKey.Escape,
        "Tab" => InputKey.Tab,
        " " => InputKey.Space,
        "Backspace" => InputKey.Backspace,
        "Delete" => InputKey.Delete,
        "+" or "=" => InputKey.Plus,
        "-" => InputKey.Minus,
        "." => InputKey.Period,
        "," => InputKey.Comma,
        "/" => InputKey.Slash,
        "\\" => InputKey.Backslash,
        ";" => InputKey.Semicolon,
        "'" => InputKey.Quote,
        "[" => InputKey.BracketLeft,
        "]" => InputKey.BracketRight,
        "`" => InputKey.Grave,
        ['F', var d] when d is >= '1' and <= '9' => InputKey.F1 + (d - '1'),
        "F10" => InputKey.F10,
        "F11" => InputKey.F11,
        "F12" => InputKey.F12,
        [var c] when c is >= 'a' and <= 'z' => InputKey.A + (c - 'a'),
        [var c] when c is >= 'A' and <= 'Z' => InputKey.A + (c - 'A'),
        [var c] when c is >= '0' and <= '9' => InputKey.D0 + (c - '0'),
        _ => InputKey.None,
    };

    private static InputKey FromCode(string code) => code switch
    {
        "Equal" or "NumpadAdd" => InputKey.Plus,
        "Minus" or "NumpadSubtract" => InputKey.Minus,
        "Period" or "NumpadDecimal" => InputKey.Period,
        "Comma" => InputKey.Comma,
        "Slash" or "NumpadDivide" => InputKey.Slash,
        "Backslash" => InputKey.Backslash,
        "Semicolon" => InputKey.Semicolon,
        "Quote" => InputKey.Quote,
        "Backquote" => InputKey.Grave,
        "BracketLeft" => InputKey.BracketLeft,
        "BracketRight" => InputKey.BracketRight,
        "NumpadEnter" => InputKey.Enter,
        "Digit0" => InputKey.D0,
        ['D', 'i', 'g', 'i', 't', var d] when d is >= '1' and <= '9' => InputKey.D1 + (d - '1'),
        ['K', 'e', 'y', var c] when c is >= 'A' and <= 'Z' => InputKey.A + (c - 'A'),
        _ => InputKey.None,
    };
}
