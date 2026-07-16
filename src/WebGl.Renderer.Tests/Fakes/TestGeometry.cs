using DIR.Lib;

namespace WebGl.Renderer.Tests.Fakes;

internal static class TestGeometry
{
    /// <summary>RectInt from edges. Routing constants through int parameters sidesteps the
    /// ambiguous (int,int)/(uint,uint) tuple → PointInt implicit conversions that constant
    /// tuples trip over.</summary>
    public static RectInt Rect(int x0, int y0, int x1, int y1) => new((x1, y1), (x0, y0));
}
