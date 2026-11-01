using Goggles3.Core;
using Xunit;

namespace Goggles3.Core.Tests;

public sealed class DiagnosticLogTests
{
    [Fact] public async Task KeepsTheLastLines_AndWritesThemOnClose()
    {
        string path = Path.Combine(Path.GetTempPath(), $"goggles3-log-{Guid.NewGuid():N}", "diagnostic.txt");
        var log = new DiagnosticLog(path, capacity: 3);
        Parallel.For(0, 10, i => log.Add("early " + i)); // any thread may log
        log.Flush();
        for (int i = 0; i < 3; i++) log.Add("last " + i);
        await log.CloseAsync();

        string[] expected = ["last 0", "last 1", "last 2"];
        Assert.Equal(expected, log.Lines.Select(l => l[9..])); // "HH:mm:ss " prefix
        Assert.Equal(expected, File.ReadAllLines(path).Select(l => l[9..]));
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact] public async Task UnwritableFile_DoesNotThrow()
    {
        string blocker = Path.GetTempFileName(); // a file where the folder should be
        var log = new DiagnosticLog(Path.Combine(blocker, "diagnostic.txt"));
        log.Add("x");
        await log.CloseAsync();
        Assert.Single(log.Lines);
        File.Delete(blocker);
    }
}
