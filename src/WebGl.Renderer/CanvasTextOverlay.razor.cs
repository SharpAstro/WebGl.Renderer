using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace WebGl.Renderer;

/// <summary>Text + selection state reported on every native input event (typing, IME
/// composition updates, paste, cut, autocorrect — anything that changes the value).</summary>
public readonly record struct CanvasOverlayInputArgs(string Value, int SelectionStart, int SelectionEnd);

/// <summary>A navigation/commit key the overlay intercepted (preventDefault'd) instead of
/// letting the input consume it: ArrowUp, ArrowDown, Enter, Escape, or Tab. Keys pressed during
/// IME composition are never forwarded.</summary>
public readonly record struct CanvasOverlayKeyArgs(string Key, bool Shift, bool Ctrl, bool Alt);

/// <summary>
/// A REAL, focusable <c>&lt;input&gt;</c> positioned over a canvas-drawn text widget — the
/// standard companion to canvas-rendered UIs (Figma / Google Docs / xterm.js all pair their
/// drawn text with a native input), because only a real focused element gets IME composition,
/// native clipboard, autocorrect, and the mobile soft keyboard.
///
/// <para>Contract: the consumer places this inside the same <c>position:relative</c> container
/// as its canvas and drives it imperatively — <see cref="ShowAsync"/> when its canvas widget
/// gains focus (inside the activating pointer event, so the browser treats the focus() as
/// user-initiated and shows the soft keyboard), <see cref="SetRectAsync"/> when the widget's
/// arranged rect moves, <see cref="SetValueAsync"/> when the canvas side rewrites the text
/// (e.g. an autocomplete commit), <see cref="HideAsync"/> on deactivate. While shown, the
/// browser input is the source of truth for the TEXT (all editing is native); the canvas side
/// mirrors it via <see cref="OnInput"/> and keeps navigation/commit semantics via
/// <see cref="OnSpecialKey"/>. v1 is a visible, consumer-styled input covering the widget —
/// not the invisible mirror variant (caret-fidelity sync) — because visible-and-native is
/// robust for every input source at the cost of a font mismatch while editing.</para>
/// </summary>
public sealed partial class CanvasTextOverlay : ComponentBase, IAsyncDisposable
{
    /// <summary>CSS class(es) carrying the visual styling (background, border, color, font).
    /// The inline style carries only mechanics (absolute positioning, hidden start).</summary>
    [Parameter]
    public string? Class { get; set; }

    /// <summary>Fires on every native value change with the new text + selection.</summary>
    [Parameter]
    public EventCallback<CanvasOverlayInputArgs> OnInput { get; set; }

    /// <summary>Fires for the intercepted navigation/commit keys (see
    /// <see cref="CanvasOverlayKeyArgs"/>); the input never sees them.</summary>
    [Parameter]
    public EventCallback<CanvasOverlayKeyArgs> OnSpecialKey { get; set; }

    /// <summary>Fires when the input loses focus by USER action (tap elsewhere, Tab away at the
    /// browser level). A programmatic <see cref="HideAsync"/> does not raise this.</summary>
    [Parameter]
    public EventCallback OnBlurred { get; set; }

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    private ElementReference _inputRef;
    private IJSObjectReference? _module;
    private DotNetObjectReference<CanvasTextOverlay>? _selfRef;

    /// <summary>True between <see cref="ShowAsync"/> and <see cref="HideAsync"/> — for hosts
    /// that reposition per frame and want a cheap gate.</summary>
    public bool IsVisible { get; private set; }

    // DynamicDependency: the three [JSInvokable] callbacks are only reached via JS interop
    // reflection, invisible to the trimmer (same pattern as WebGlCanvas.OnCanvasMetricsAsync).
    [DynamicDependency(nameof(OnOverlayInput))]
    [DynamicDependency(nameof(OnOverlayKey))]
    [DynamicDependency(nameof(OnOverlayBlur))]
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }
        // Version-stamped for the same cache-busting reason as WebGlRenderer.CreateAsync's
        // webgl-renderer.js import (the path is not fingerprinted; see the comment there).
        var v = Uri.EscapeDataString(typeof(CanvasTextOverlay).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is [System.Reflection.AssemblyInformationalVersionAttribute a, ..] ? a.InformationalVersion : "0");
        _module = await JS.InvokeAsync<IJSObjectReference>(
            "import", $"./_content/WebGl.Renderer/webgl-text-overlay.js?v={v}");
        _selfRef = DotNetObjectReference.Create(this);
        await _module.InvokeVoidAsync("attach", _inputRef, _selfRef);
    }

    /// <summary>Shows the input over the widget rect (CSS px, relative to the shared positioned
    /// container), seeds text + caret, and focuses it — call from the activating pointer event.
    /// <paramref name="fontSizePx"/> 0 derives the size from the rect height.</summary>
    public async ValueTask ShowAsync(string value, int caretPos,
        double x, double y, double width, double height, double fontSizePx = 0)
    {
        if (_module is null)
        {
            return; // pre-first-render call; nothing to focus yet
        }
        IsVisible = true;
        await _module.InvokeVoidAsync("show", _inputRef, value, caretPos, x, y, width, height, fontSizePx);
    }

    /// <summary>Repositions the visible input; call when the widget's arranged rect moved.</summary>
    public async ValueTask SetRectAsync(double x, double y, double width, double height, double fontSizePx = 0)
    {
        if (_module is null || !IsVisible)
        {
            return;
        }
        await _module.InvokeVoidAsync("setRect", _inputRef, x, y, width, height, fontSizePx);
    }

    /// <summary>Pushes a canvas-side text rewrite (autocomplete commit, programmatic clear) into
    /// the input without an <see cref="OnInput"/> echo.</summary>
    public async ValueTask SetValueAsync(string value, int caretPos)
    {
        if (_module is null)
        {
            return;
        }
        await _module.InvokeVoidAsync("setValue", _inputRef, value, caretPos);
    }

    /// <summary>Hides + blurs the input. Does not raise <see cref="OnBlurred"/>.</summary>
    public async ValueTask HideAsync()
    {
        if (_module is null || !IsVisible)
        {
            return;
        }
        IsVisible = false;
        await _module.InvokeVoidAsync("hide", _inputRef);
    }

    [JSInvokable]
    public Task OnOverlayInput(string value, int selectionStart, int selectionEnd)
        => OnInput.InvokeAsync(new CanvasOverlayInputArgs(value, selectionStart, selectionEnd));

    [JSInvokable]
    public Task OnOverlayKey(string key, bool shift, bool ctrl, bool alt)
        => OnSpecialKey.InvokeAsync(new CanvasOverlayKeyArgs(key, shift, ctrl, alt));

    [JSInvokable]
    public Task OnOverlayBlur()
    {
        IsVisible = false;
        return OnBlurred.InvokeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("detach", _inputRef);
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Page already torn down — nothing left to detach from.
            }
        }
        _selfRef?.Dispose();
    }
}
