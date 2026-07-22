using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DIR.Lib;
using Microsoft.AspNetCore.Components;

namespace WebGl.Renderer;

/// <summary>
/// One selectable text run for <see cref="CanvasTextLayer"/>, in CSS pixels relative to the shared
/// position:relative canvas container. Typically mapped 1:1 from a widget's
/// <c>SelectableTextRegion</c>s (which are in backing-buffer pixels) by dividing by the
/// device-pixel-ratio. A readonly record struct so hosts can cheaply <c>SequenceEqual</c> two frames'
/// run lists to skip re-rendering when nothing changed.
/// <para>
/// <see cref="Href"/>, when non-null, renders the run as a real <c>&lt;a href&gt;</c> (new-tab,
/// <c>rel="noopener"</c>) instead of a plain selectable span, so the browser handles open/copy-link
/// natively. Mirrors <c>SelectableTextRegion.Href</c>.
/// </para>
/// </summary>
public readonly record struct OverlayTextRun(
    float X, float Y, float Width, float Height,
    string Text,
    float FontSizePx,
    RGBAColor32 Color,
    TextAlign HorizontalAlign = TextAlign.Near,
    TextAlign VerticalAlign = TextAlign.Center,
    string? Href = null);

/// <summary>
/// A retained layer of read-only, natively-selectable text runs rendered as real DOM over a
/// <see cref="WebGlCanvas"/>. Pure Blazor rendering -- no JS interop: the host updates
/// <see cref="Runs"/> after each canvas frame (from the widget's registered selectable-text
/// regions) and lets Blazor diff the spans. Pair with
/// <c>Renderer&lt;TSurface&gt;.HostRendersSelectableText = true</c> so the canvas skips rasterizing
/// the same text (this layer's DOM text is then the only copy on screen).
/// </summary>
public sealed partial class CanvasTextLayer : ComponentBase
{
    /// <summary>The text runs to display, in paint order. Null/empty renders nothing.</summary>
    [Parameter]
    public IReadOnlyList<OverlayTextRun>? Runs { get; set; }

    /// <summary>
    /// CSS font-family for all runs (e.g. <c>"'DejaVu Sans', system-ui, sans-serif"</c>). Point an
    /// @font-face at the same font file the canvas atlas uses so DOM and rastered text match.
    /// </summary>
    [Parameter]
    public string FontFamily { get; set; } = "system-ui, sans-serif";

    /// <summary>Optional CSS class for the layer container.</summary>
    [Parameter]
    public string? Class { get; set; }

    // Builds the per-run inline style. Invariant culture throughout -- a comma decimal separator
    // would corrupt the CSS. line-height 1.3 mirrors the renderer's lineHeight = fontSize * 1.3
    // convention so multi-line runs (explicit \n, rendered via white-space:pre) space identically.
    private string RunStyle(in OverlayTextRun run)
    {
        var sb = new StringBuilder(256);
        sb.Append("position:absolute;overflow:hidden;white-space:pre;line-height:1.3;");
        sb.Append("pointer-events:auto;user-select:text;-webkit-user-select:text;");
        // A link run gets pointer affordance + an underline so it reads as clickable; a plain span keeps
        // the text caret and no decoration (an <a> would otherwise inherit the browser default underline
        // AND link colour -- we set colour explicitly below, so only the underline needs asserting here).
        sb.Append(run.Href is { Length: > 0 } ? "cursor:pointer;text-decoration:underline;" : "cursor:text;");
        sb.Append(CultureInfo.InvariantCulture,
            $"left:{run.X:0.##}px;top:{run.Y:0.##}px;width:{run.Width:0.##}px;height:{run.Height:0.##}px;");
        sb.Append(CultureInfo.InvariantCulture, $"font-size:{run.FontSizePx:0.##}px;");
        sb.Append("font-family:").Append(FontFamily).Append(';');
        sb.Append(CultureInfo.InvariantCulture,
            $"color:rgba({run.Color.Red},{run.Color.Green},{run.Color.Blue},{run.Color.Alpha / 255f:0.###});");
        // TextAlign -> flex mapping, same Near/Center/Far semantics as the raster path.
        sb.Append("display:flex;align-items:").Append(FlexAlign(run.VerticalAlign));
        sb.Append(";justify-content:").Append(FlexAlign(run.HorizontalAlign)).Append(';');
        return sb.ToString();
    }

    private static string FlexAlign(TextAlign align) => align switch
    {
        TextAlign.Center => "center",
        TextAlign.Far => "flex-end",
        _ => "flex-start",
    };
}
