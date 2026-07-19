namespace WebGl.Renderer;

/// <summary>
/// A two-finger pinch on a <see cref="WebGlCanvas"/>. <see cref="Scale"/> is the RELATIVE per-event
/// factor (the current inter-finger distance / the distance at the previous pinch event) - fingers
/// spreading apart give <see cref="Scale"/> &gt; 1 - matching the SDL touch convention the shared
/// sky-map pinch handler consumes (it zooms by <c>1 / Scale</c>). <see cref="X"/>/<see cref="Y"/> are
/// the finger MIDPOINT already mapped into backing-buffer space (the on-screen point to anchor the
/// zoom around). There is no raw Blazor event: touch is handled JS-side (webgl-canvas.js, because
/// <c>touch-action:none</c> suppresses the compat mouse events) and synthesized across the interop
/// boundary as flat scalars, so no custom type needs JSON (de)serialization (trim/AOT-safe).
/// </summary>
public readonly record struct CanvasPinchEventArgs(double Scale, int X, int Y);
