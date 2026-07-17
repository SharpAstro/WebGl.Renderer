namespace WebGl.Renderer;

/// <summary>
/// Hi-dpi canvas metrics reported by <see cref="WebGlCanvas"/>. <see cref="BackingWidth"/>/
/// <see cref="BackingHeight"/> are the canvas's actual backing-buffer pixel dimensions (CSS
/// layout size × <see cref="DevicePixelRatio"/>, rounded, clamped ≥ 1) — pass these directly to
/// <see cref="WebGlRenderer.CreateAsync"/>/<see cref="WebGlRenderer.Resize"/> and to any
/// widget/UI constructed at backing resolution. <see cref="CssWidth"/>/<see cref="CssHeight"/>
/// are the raw laid-out CSS box (informational; fractional).
/// </summary>
public readonly record struct CanvasMetrics(
    uint BackingWidth,
    uint BackingHeight,
    double CssWidth,
    double CssHeight,
    double DevicePixelRatio);
