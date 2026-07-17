using Microsoft.AspNetCore.Components.Web;

namespace WebGl.Renderer;

/// <summary>
/// A wheel event on a <see cref="WebGlCanvas"/>, with <see cref="X"/>/<see cref="Y"/> already
/// mapped into backing-buffer space (<c>Original.OffsetX/Y × devicePixelRatio</c>, rounded).
/// <see cref="DeltaY"/>/<see cref="DeltaMode"/> are forwarded RAW from the browser event —
/// sign and scale conventions (e.g. an SDL-style positive-is-up <c>-DeltaY/100</c>) are the
/// consumer's policy, not the component's. <see cref="Original"/> is the raw Blazor event.
/// </summary>
public readonly record struct CanvasWheelEventArgs(int X, int Y, double DeltaY, long DeltaMode, WheelEventArgs Original);
