using Proteus.Core.Models;
using Proteus.Core.Rules;
using Proteus.Core.Tokens;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// Manual check of metadata tokens against real files. Does nothing unless PROTEUS_PROBE holds
/// a ';'-separated list of file paths. Read-only.
/// </summary>
public class RealFileProbe(ITestOutputHelper output)
{
    [Fact]
    public void Probe()
    {
        var list = Environment.GetEnvironmentVariable("PROTEUS_PROBE");
        if (string.IsNullOrEmpty(list)) return;
        string[] tokens =
        [
            "<type>", "<owner>", "<video:duration>", "<video:resolution>", "<video:quality>", "<video:fps>", "<video:bitrate>",
            "<video:date:yyyy-MM-dd>", "<audio:duration>", "<audio:bitrate>", "<audio:samplerate>", "<audio:channels>",
            "<music:artist>", "<music:album>", "<music:title>", "<music:track:2>", "<music:year>",
            "<doc:title>", "<doc:author>", "<doc:pages>", "<doc:words>", "<doc:company>", "<doc:created:yyyy-MM-dd>", "<doc:app>",
        ];
        foreach (var path in list.Split(';'))
        {
            var e = new FileEntry(path);
            output.WriteLine($"== {Path.GetExtension(path)}");
            var ctx = new RuleContext(e, 0, 0, 1);
            foreach (var t in tokens)
            {
                var v = TokenEngine.Expand(t, ctx);
                if (v.Length > 0) output.WriteLine($"  {t} = {v}");
            }
        }
    }
}
