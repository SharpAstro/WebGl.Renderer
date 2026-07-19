using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace WebGl.Renderer;

/// <summary>
/// Reusable hi-dpi canvas host: measures devicePixelRatio + laid-out CSS size, sizes the canvas
/// backing buffer accordingly, and reports <see cref="CanvasMetrics"/> to the parent via
/// <see cref="OnReady"/> (first measurement) and <see cref="OnResized"/> (every one after —
/// window resize, CSS reflow, or a devicePixelRatio change, e.g. dragging the window across
/// differently-scaled monitors). This component does NOT create a renderer itself — the parent
/// calls <see cref="WebGlRenderer.CreateAsync"/> (or builds a CPU renderer) from
/// <see cref="OnReady"/>, supplying whatever backend-specific state it needs (disk caches, …).
///
/// <para><b>canvas.width/height ownership:</b> before a renderer exists, THIS component (via
/// webgl-canvas.js) is the sole writer of the backing buffer. After the parent creates a
/// <see cref="WebGlRenderer"/>, webgl-renderer.js's <c>SetViewport</c> handler (fired by
/// <see cref="WebGlRenderer.Resize"/>) ALSO writes canvas.width/height on every subsequent
/// resize — to the exact same numbers this component already wrote a moment earlier (both derive
/// from the same <see cref="CanvasMetrics"/>). Last-write-wins with identical values, so there is
/// no divergence; the component only measures and reports, and the parent decides when to call
/// <c>Resize</c>. For a CPU-backed (2D putImageData) canvas there is no other writer at all, so
/// this component is authoritative throughout.</para>
/// </summary>
public sealed partial class WebGlCanvas : ComponentBase, IHandleEvent, IAsyncDisposable
{
    /// <summary>The canvas element's DOM id — also what the parent passes to
    /// <see cref="WebGlRenderer.CreateAsync"/> / any other id-based JS interop on this element.</summary>
    [Parameter, EditorRequired]
    public string Id { get; set; } = "";

    /// <summary>Extra CSS class(es). This component only sets gesture-related inline style
    /// (touch-action/user-select); display sizing is the consumer's job — size the laid-out CSS
    /// box (e.g. <c>max-width:100%; aspect-ratio:W/H; height:auto</c>) and the backing buffer
    /// follows it at device resolution.</summary>
    [Parameter]
    public string? Class { get; set; }

    /// <summary>Rendered as the canvas's <c>tabindex</c> (0 = focusable, for keyboard input).</summary>
    [Parameter]
    public int TabIndex { get; set; }

    /// <summary>Clamp for devicePixelRatio — bounds backing-buffer memory against pathological
    /// reports (deep browser zoom compounds into devicePixelRatio). 3.0 covers real hardware.</summary>
    [Parameter]
    public double MaxDevicePixelRatio { get; set; } = 3.0;

    /// <summary>Fires exactly once, after the first layout measurement. Create the renderer (and
    /// any resolution-dependent UI state) here — NOT in the parent's OnInitializedAsync, since
    /// the canvas isn't guaranteed laid out (or even in the DOM) until this fires.</summary>
    [Parameter]
    public EventCallback<CanvasMetrics> OnReady { get; set; }

    /// <summary>Fires on every subsequent layout or devicePixelRatio change. Call the renderer's
    /// <c>Resize(BackingWidth, BackingHeight)</c>, resize resolution-dependent UI, re-render.</summary>
    [Parameter]
    public EventCallback<CanvasMetrics> OnResized { get; set; }

    /// <summary>A primary-surface press (bound to <c>mousedown</c> since 1.2 — 1.1 bound
    /// <c>click</c>, which fires on release and cannot start a drag; consumers that want
    /// click/release semantics use <see cref="OnPointerUp"/>). Coordinates already mapped to
    /// backing-buffer space (CSS offset × devicePixelRatio) — feed straight into pixel-space
    /// hit-testing. <c>Original.Detail</c> carries the click count (the reason these are the
    /// compatibility mouse events, not pointer events, whose <c>detail</c> is spec'd 0).</summary>
    [Parameter]
    public EventCallback<CanvasPointerEventArgs> OnPointerDown { get; set; }

    /// <summary>Pointer motion, backing-space mapped like <see cref="OnPointerDown"/>. With
    /// <see cref="CapturePointer"/> (default) a drag keeps reporting after the pointer leaves
    /// the canvas — coordinates can then be negative / beyond the backing size; clamp consumer-side.</summary>
    [Parameter]
    public EventCallback<CanvasPointerEventArgs> OnPointerMove { get; set; }

    /// <summary>Pointer release, backing-space mapped like <see cref="OnPointerDown"/>.</summary>
    [Parameter]
    public EventCallback<CanvasPointerEventArgs> OnPointerUp { get; set; }

    /// <summary>Wheel scroll at a backing-space position; deltas forwarded raw (see
    /// <see cref="CanvasWheelEventArgs"/>).</summary>
    [Parameter]
    public EventCallback<CanvasWheelEventArgs> OnWheel { get; set; }

    /// <summary>Forwarded verbatim — no coordinate mapping applies to key events.</summary>
    [Parameter]
    public EventCallback<KeyboardEventArgs> OnKeyDown { get; set; }

    /// <summary>A two-finger pinch (touchscreen). Fires per <c>touchmove</c> while two fingers are
    /// down; see <see cref="CanvasPinchEventArgs"/> for the relative-scale + backing-space-midpoint
    /// contract. SINGLE-finger touch is delivered through the normal <see cref="OnPointerDown"/> /
    /// <see cref="OnPointerMove"/> / <see cref="OnPointerUp"/> callbacks (a synthesized left-button
    /// press), so a consumer that already handles mouse drag gets touch pan for free — this fires only
    /// for the two-finger gesture.</summary>
    [Parameter]
    public EventCallback<CanvasPinchEventArgs> OnPinch { get; set; }

    /// <summary>Fires once when a pinch ends — a finger lifts and the count drops below two.</summary>
    [Parameter]
    public EventCallback OnPinchEnd { get; set; }

    /// <summary>Captures the pointer on a primary-button press (<c>setPointerCapture</c> in
    /// webgl-canvas.js), so mousemove/mouseup keep retargeting to the canvas after the pointer
    /// leaves it — the SDL mouse-capture analogue drags need. Release is implicit on pointerup.</summary>
    [Parameter]
    public bool CapturePointer { get; set; } = true;

    /// <summary>Focuses the canvas after the first metrics report, so keyboard input works
    /// without a click first (pair with a non-negative <see cref="TabIndex"/>).</summary>
    [Parameter]
    public bool AutoFocus { get; set; }

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    private ElementReference _canvasRef;
    private IJSObjectReference? _module;
    private DotNetObjectReference<WebGlCanvas>? _selfRef;
    private CanvasMetrics _metrics;
    private bool _ready;

    /// <summary>The most recent metrics report, for parents that need it outside the callbacks.</summary>
    public CanvasMetrics Metrics => _metrics;

    /// <summary>Focuses the canvas (e.g. so keyboard input works without a click first).</summary>
    public ValueTask FocusAsync() => _canvasRef.FocusAsync();

    // DynamicDependency: OnCanvasMetricsAsync is only reached via JS interop reflection
    // (DotNetObjectReference.invokeMethodAsync), invisible to the trimmer's static analysis.
    // Anchoring it to OnAfterRenderAsync — the method that hands the reference to JS — keeps it
    // alive for consumers that don't root this assembly with TrimmerRootAssembly.
    [DynamicDependency(nameof(OnCanvasMetricsAsync))]
    [DynamicDependency(nameof(OnTouchPointerDownAsync))]
    [DynamicDependency(nameof(OnTouchPointerMoveAsync))]
    [DynamicDependency(nameof(OnTouchPointerUpAsync))]
    [DynamicDependency(nameof(OnTouchPinchAsync))]
    [DynamicDependency(nameof(OnTouchPinchEndAsync))]
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }
        // Version-stamped for the same cache-busting reason as WebGlRenderer.CreateAsync's
        // webgl-renderer.js import (the path is not fingerprinted; see the comment there).
        var v = Uri.EscapeDataString(typeof(WebGlCanvas).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is [System.Reflection.AssemblyInformationalVersionAttribute a, ..] ? a.InformationalVersion : "0");
        _module = await JS.InvokeAsync<IJSObjectReference>(
            "import", $"./_content/WebGl.Renderer/webgl-canvas.js?v={v}");
        _selfRef = DotNetObjectReference.Create(this);
        await _module.InvokeVoidAsync("attach", _canvasRef, _selfRef, MaxDevicePixelRatio, CapturePointer);
    }

    /// <summary>
    /// webgl-canvas.js's single callback: invoked once immediately on attach (the "ready" report)
    /// and again on every ResizeObserver/dpr-change report. Deliberately flat scalar parameters —
    /// no DTO crosses the JS boundary, so JSInterop never needs reflection-based JSON
    /// deserialization of a custom type (trim/AOT-safe without a JsonSerializerContext).
    /// </summary>
    [JSInvokable]
    public async Task OnCanvasMetricsAsync(
        double backingWidth, double backingHeight, double cssWidth, double cssHeight, double devicePixelRatio)
    {
        var metrics = new CanvasMetrics(
            (uint)Math.Max(1, Math.Round(backingWidth)),
            (uint)Math.Max(1, Math.Round(backingHeight)),
            cssWidth, cssHeight, devicePixelRatio);

        if (_ready && metrics == _metrics)
        {
            return; // e.g. a scroll-induced ResizeObserver tick with no real change
        }
        var isFirst = !_ready;
        _metrics = metrics;
        _ready = true;
        if (isFirst && AutoFocus)
        {
            await _canvasRef.FocusAsync();
        }
        await (isFirst ? OnReady : OnResized).InvokeAsync(metrics);
    }

    // Blazor reports OffsetX/Y in CSS pixels; hit-testing happens in backing-buffer space.
    // Shared by down/move/up/wheel so every callback agrees on the mapping.
    private CanvasPointerEventArgs MapToBacking(MouseEventArgs e)
    {
        var dpr = _metrics.DevicePixelRatio > 0 ? _metrics.DevicePixelRatio : 1;
        return new CanvasPointerEventArgs((int)Math.Round(e.OffsetX * dpr), (int)Math.Round(e.OffsetY * dpr), e);
    }

    private Task HandlePointerDownAsync(MouseEventArgs e) => OnPointerDown.InvokeAsync(MapToBacking(e));

    private Task HandlePointerMoveAsync(MouseEventArgs e)
        => OnPointerMove.HasDelegate ? OnPointerMove.InvokeAsync(MapToBacking(e)) : Task.CompletedTask;

    private Task HandlePointerUpAsync(MouseEventArgs e)
        => OnPointerUp.HasDelegate ? OnPointerUp.InvokeAsync(MapToBacking(e)) : Task.CompletedTask;

    private Task HandleWheelAsync(WheelEventArgs e)
    {
        if (!OnWheel.HasDelegate)
        {
            return Task.CompletedTask;
        }
        var p = MapToBacking(e);
        return OnWheel.InvokeAsync(new CanvasWheelEventArgs(p.X, p.Y, e.DeltaY, e.DeltaMode, e));
    }

    private Task HandleKeyDownAsync(KeyboardEventArgs e) => OnKeyDown.InvokeAsync(e);

    // ── Touch → pointer/pinch bridge (JSInvokable, called from webgl-canvas.js) ──
    // touch-action:none (set on the canvas so the page doesn't scroll/zoom under the fingers) ALSO
    // suppresses the browser's compatibility mouse events, so the @onmouse* bindings never fire for a
    // finger. webgl-canvas.js listens for raw touch events and calls these: a SINGLE finger maps to the
    // same pointer callbacks the mouse uses (so a consumer handling mouse drag gets touch pan/tap for
    // free), TWO fingers drive OnPinch/OnPinchEnd. Coordinates arrive ALREADY mapped to backing space
    // (getBoundingClientRect + devicePixelRatio, JS-side), so no MapToBacking hop here.

    // A finger is a no-modifier left-button, single-click press: the pointer callbacks expose Original
    // for Button/Detail/modifiers, and this is what a tap looks like to a mouse-shaped consumer.
    private static MouseEventArgs TouchMouseArgs() => new() { Button = 0, Detail = 1 };

    /// <summary>JS bridge: a single finger pressed — maps to <see cref="OnPointerDown"/>.</summary>
    [JSInvokable]
    public Task OnTouchPointerDownAsync(double x, double y)
        => OnPointerDown.HasDelegate
            ? OnPointerDown.InvokeAsync(new CanvasPointerEventArgs((int)x, (int)y, TouchMouseArgs()))
            : Task.CompletedTask;

    /// <summary>JS bridge: the single finger moved — maps to <see cref="OnPointerMove"/>.</summary>
    [JSInvokable]
    public Task OnTouchPointerMoveAsync(double x, double y)
        => OnPointerMove.HasDelegate
            ? OnPointerMove.InvokeAsync(new CanvasPointerEventArgs((int)x, (int)y, TouchMouseArgs()))
            : Task.CompletedTask;

    /// <summary>JS bridge: the single finger lifted — maps to <see cref="OnPointerUp"/>.</summary>
    [JSInvokable]
    public Task OnTouchPointerUpAsync(double x, double y)
        => OnPointerUp.HasDelegate
            ? OnPointerUp.InvokeAsync(new CanvasPointerEventArgs((int)x, (int)y, TouchMouseArgs()))
            : Task.CompletedTask;

    /// <summary>JS bridge: a two-finger pinch step — maps to <see cref="OnPinch"/>.</summary>
    [JSInvokable]
    public Task OnTouchPinchAsync(double scale, double x, double y)
        => OnPinch.HasDelegate
            ? OnPinch.InvokeAsync(new CanvasPinchEventArgs(scale, (int)x, (int)y))
            : Task.CompletedTask;

    /// <summary>JS bridge: the pinch ended — maps to <see cref="OnPinchEnd"/>.</summary>
    [JSInvokable]
    public Task OnTouchPinchEndAsync()
        => OnPinchEnd.HasDelegate ? OnPinchEnd.InvokeAsync() : Task.CompletedTask;

    /// <summary>
    /// Suppresses ComponentBase's render-after-every-DOM-event: this component's markup is a
    /// single attribute-stable canvas element, so re-rendering it per mousemove is pure waste
    /// on what is typically a render-loop-driven surface. Consumers' own re-render behaviour is
    /// untouched (EventCallback notifies the receiver, not this component).
    /// </summary>
    Task IHandleEvent.HandleEventAsync(EventCallbackWorkItem callback, object? arg) => callback.InvokeAsync(arg);

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("detach", _canvasRef);
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
