using DIR.Lib;
using Microsoft.AspNetCore.Components.Web;
using Shouldly;
using Xunit;

namespace WebGl.Renderer.Tests;

public sealed class WebInputMappingTests
{
    [Theory]
    [InlineData("ArrowUp", InputKey.Up)]
    [InlineData("ArrowDown", InputKey.Down)]
    [InlineData("ArrowLeft", InputKey.Left)]
    [InlineData("ArrowRight", InputKey.Right)]
    [InlineData("Home", InputKey.Home)]
    [InlineData("End", InputKey.End)]
    [InlineData("PageUp", InputKey.PageUp)]
    [InlineData("PageDown", InputKey.PageDown)]
    [InlineData("Enter", InputKey.Enter)]
    [InlineData("Escape", InputKey.Escape)]
    [InlineData("Tab", InputKey.Tab)]
    [InlineData(" ", InputKey.Space)]
    [InlineData("Backspace", InputKey.Backspace)]
    [InlineData("Delete", InputKey.Delete)]
    [InlineData("+", InputKey.Plus)]
    [InlineData("=", InputKey.Plus)]
    [InlineData("-", InputKey.Minus)]
    [InlineData(".", InputKey.Period)]
    [InlineData(",", InputKey.Comma)]
    [InlineData("/", InputKey.Slash)]
    [InlineData("\\", InputKey.Backslash)]
    [InlineData(";", InputKey.Semicolon)]
    [InlineData("'", InputKey.Quote)]
    [InlineData("[", InputKey.BracketLeft)]
    [InlineData("]", InputKey.BracketRight)]
    [InlineData("`", InputKey.Grave)]
    [InlineData("F1", InputKey.F1)]
    [InlineData("F9", InputKey.F9)]
    [InlineData("F10", InputKey.F10)]
    [InlineData("F12", InputKey.F12)]
    [InlineData("a", InputKey.A)]
    [InlineData("Z", InputKey.Z)]
    [InlineData("0", InputKey.D0)]
    [InlineData("9", InputKey.D9)]
    public void KeyTable(string key, InputKey expected)
        => WebInputMapping.ToInputKey(key, null).ShouldBe(expected);

    [Theory]
    [InlineData("Equal", InputKey.Plus)]
    [InlineData("Minus", InputKey.Minus)]
    [InlineData("Period", InputKey.Period)]
    [InlineData("Comma", InputKey.Comma)]
    [InlineData("Slash", InputKey.Slash)]
    [InlineData("Backslash", InputKey.Backslash)]
    [InlineData("Semicolon", InputKey.Semicolon)]
    [InlineData("Quote", InputKey.Quote)]
    [InlineData("Backquote", InputKey.Grave)]
    [InlineData("BracketLeft", InputKey.BracketLeft)]
    [InlineData("BracketRight", InputKey.BracketRight)]
    [InlineData("Digit0", InputKey.D0)]
    [InlineData("Digit1", InputKey.D1)]
    [InlineData("Digit9", InputKey.D9)]
    [InlineData("KeyA", InputKey.A)]
    [InlineData("KeyZ", InputKey.Z)]
    public void CodeFallbackWhenKeyIsUnknown(string code, InputKey expected)
        => WebInputMapping.ToInputKey("Dead", code).ShouldBe(expected);

    [Theory]
    [InlineData("!", "Digit1", InputKey.D1)]
    [InlineData("_", "Minus", InputKey.Minus)]
    [InlineData("?", "Slash", InputKey.Slash)]
    [InlineData("<", "Comma", InputKey.Comma)]
    [InlineData(":", "Semicolon", InputKey.Semicolon)]
    [InlineData("~", "Backquote", InputKey.Grave)]
    [InlineData("&", "Digit1", InputKey.D1)]
    [InlineData("=", "Equal", InputKey.Plus)]
    public void ShiftedOrLayoutChangedKeyLandsWhereSdlLands(string key, string code, InputKey expected)
        => WebInputMapping.ToInputKey(key, code).ShouldBe(expected);

    [Theory]
    [InlineData("+", "NumpadAdd", InputKey.Plus)]
    [InlineData("-", "NumpadSubtract", InputKey.Minus)]
    [InlineData(".", "NumpadDecimal", InputKey.Period)]
    [InlineData("/", "NumpadDivide", InputKey.Slash)]
    [InlineData("Enter", "NumpadEnter", InputKey.Enter)]
    [InlineData("7", "Numpad7", InputKey.D7)]
    [InlineData("0", "Numpad0", InputKey.D0)]
    [InlineData("Home", "Numpad7", InputKey.Home)]
    [InlineData("ArrowUp", "Numpad8", InputKey.Up)]
    [InlineData("*", "NumpadMultiply", InputKey.None)]
    [InlineData("Clear", "Numpad5", InputKey.None)]
    [InlineData("Insert", "Numpad0", InputKey.None)]
    public void KeypadKeys(string key, string code, InputKey expected)
        => WebInputMapping.ToInputKey(key, code).ShouldBe(expected);

    [Theory]
    [InlineData("NumpadAdd", InputKey.Plus)]
    [InlineData("NumpadSubtract", InputKey.Minus)]
    [InlineData("NumpadDecimal", InputKey.Period)]
    [InlineData("NumpadDivide", InputKey.Slash)]
    [InlineData("NumpadEnter", InputKey.Enter)]
    public void KeypadCodeAloneStillMaps(string code, InputKey expected)
        => WebInputMapping.ToInputKey("Unidentified", code).ShouldBe(expected);

    [Theory]
    [InlineData("Shift", "ShiftLeft")]
    [InlineData("F13", "F13")]
    [InlineData("", null)]
    [InlineData("Unidentified", null)]
    public void UnknownKeyIsNone(string key, string? code)
        => WebInputMapping.ToInputKey(key, code).ShouldBe(InputKey.None);

    [Fact]
    public void ModifiersCombine()
    {
        WebInputMapping.ToInputModifier(false, false, false).ShouldBe(InputModifier.None);
        WebInputMapping.ToInputModifier(true, false, false).ShouldBe(InputModifier.Shift);
        WebInputMapping.ToInputModifier(false, true, false).ShouldBe(InputModifier.Ctrl);
        WebInputMapping.ToInputModifier(false, false, true).ShouldBe(InputModifier.Alt);
        WebInputMapping.ToInputModifier(true, true, true).ShouldBe(InputModifier.Shift | InputModifier.Ctrl | InputModifier.Alt);
    }

    [Fact]
    public void ToInputReturnsKeyAndModifiers()
    {
        var (key, mods) = WebInputMapping.ToInput(new KeyboardEventArgs { Key = "!", Code = "Digit1", ShiftKey = true, CtrlKey = true });
        key.ShouldBe(InputKey.D1);
        mods.ShouldBe(InputModifier.Shift | InputModifier.Ctrl);
    }

    // The InputKey values SdlInputMapping produces: every InputKey except None and BracketLeft/BracketRight
    // (this project cannot reference SDL, so the set is held here). Each must be reachable from a browser
    // (Key, Code) pair listed below, with Key the unshifted character of that physical key.
    [Fact]
    public void EveryKeySdlProducesIsReachableFromTheBrowser()
    {
        var sdlKeys = Enum.GetValues<InputKey>()
            .Where(k => k is not (InputKey.None or InputKey.BracketLeft or InputKey.BracketRight))
            .ToHashSet();

        var browser = new List<(string Key, string Code)>
        {
            ("ArrowUp", "ArrowUp"), ("ArrowDown", "ArrowDown"), ("ArrowLeft", "ArrowLeft"), ("ArrowRight", "ArrowRight"),
            ("Home", "Home"), ("End", "End"), ("PageUp", "PageUp"), ("PageDown", "PageDown"),
            ("Enter", "Enter"), ("Escape", "Escape"), ("Tab", "Tab"), (" ", "Space"),
            ("Backspace", "Backspace"), ("Delete", "Delete"),
            ("=", "Equal"), ("-", "Minus"), (".", "Period"), (",", "Comma"), ("/", "Slash"),
            ("\\", "Backslash"), (";", "Semicolon"), ("'", "Quote"), ("`", "Backquote"),
        };
        for (var c = 'a'; c <= 'z'; c++) browser.Add((c.ToString(), "Key" + char.ToUpperInvariant(c)));
        for (var d = 0; d <= 9; d++) browser.Add((d.ToString(), "Digit" + d));
        for (var f = 1; f <= 12; f++) browser.Add(("F" + f, "F" + f));

        var reached = browser.Select(p => WebInputMapping.ToInputKey(p.Key, p.Code)).ToHashSet();
        sdlKeys.Except(reached).ShouldBeEmpty();
    }
}
