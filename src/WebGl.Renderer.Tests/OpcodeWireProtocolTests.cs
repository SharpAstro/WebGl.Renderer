using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace WebGl.Renderer.Tests;

/// <summary>
/// The command stream is a wire protocol between <see cref="Opcode"/> and the <c>OP</c> table in
/// <c>webgl-renderer.js</c>, and nothing else holds the two together: a number that drifts on one side
/// makes the JS interpreter throw "unknown opcode" at runtime in the browser, or worse, run the WRONG
/// command. This reads the shipped JS (linked into the test output) and requires the same names with the
/// same numbers, both ways.
/// </summary>
public sealed partial class OpcodeWireProtocolTests
{
    [GeneratedRegex(@"const OP = \{(?<body>[^}]*)\};", RegexOptions.Singleline)]
    private static partial Regex OpTable();

    [GeneratedRegex(@"(?<name>\w+)\s*:\s*(?<value>\d+)")]
    private static partial Regex OpEntry();

    [Fact]
    public void TheJsOpTableMatchesTheOpcodeEnum()
    {
        var js = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "webgl-renderer.js"));
        var table = OpTable().Match(js);
        table.Success.ShouldBeTrue("webgl-renderer.js has a `const OP = { ... };` table");

        var fromJs = OpEntry().Matches(table.Groups["body"].Value)
            .ToDictionary(m => m.Groups["name"].Value, m => int.Parse(m.Groups["value"].Value));
        var fromEnum = Enum.GetValues<Opcode>().ToDictionary(o => o.ToString(), o => (int)o);

        fromJs.OrderBy(kv => kv.Value).ShouldBe(fromEnum.OrderBy(kv => kv.Value));
    }
}
