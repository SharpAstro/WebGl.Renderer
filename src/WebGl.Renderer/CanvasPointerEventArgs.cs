using Microsoft.AspNetCore.Components.Web;

namespace WebGl.Renderer;

/// <summary>
/// A pointer-down on a <see cref="WebGlCanvas"/>, with <see cref="X"/>/<see cref="Y"/> already
/// mapped into backing-buffer space (<c>Original.OffsetX/Y × devicePixelRatio</c>, rounded) —
/// feed straight into pixel-space hit-testing (e.g. a game UI's HandleMouseDown), no further
/// scaling needed. <see cref="Original"/> is the raw Blazor event (modifiers, button, …).
/// </summary>
public readonly record struct CanvasPointerEventArgs(int X, int Y, MouseEventArgs Original);
