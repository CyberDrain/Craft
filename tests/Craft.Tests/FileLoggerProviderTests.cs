using System.Text.RegularExpressions;
using Craft.Configuration;
using Craft.Hosting;

namespace Craft.Tests;

public class FileLoggerProviderTests
{
    [Fact]
    public void AFlood_IsBoundedByDroppingLines_AndTheKeptLinesStayInOrder()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"craft-log-{Guid.NewGuid():N}");
        const int Producers = 8, PerProducer = 250_000;
        var provider = new FileLoggerProvider(new FileLoggingSettings { Directory = dir, MaxFileSizeMB = 1000 });
        var maxDepth = 0;
        Parallel.For(0, Producers, new ParallelOptions { MaxDegreeOfParallelism = Producers }, p =>
        {
            for (var i = 0; i < PerProducer; i++)
            {
                provider.WriteLine($"line {p} {i}", null);
                if ((i & 1023) == 0) InterlockedMax(ref maxDepth, provider.QueueDepth);
            }
        });
        provider.Dispose();

        try
        {
            Assert.True(maxDepth <= FileLoggerProvider.QueueCapacity, $"the log queue held {maxDepth} lines");
            var last = Enumerable.Repeat(-1, Producers).ToArray();
            long kept = 0, dropped = 0;
            foreach (var line in File.ReadLines(Path.Combine(dir, "craft.log")))
            {
                if (Regex.Match(line, @"Dropped (\d+) log lines") is { Success: true } d)
                {
                    dropped += long.Parse(d.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    continue;
                }
                var m = Regex.Match(line, @"line (\d+) (\d+)$");
                int p = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), i = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                Assert.True(i > last[p], $"producer {p}: line {i} was written after line {last[p]}");
                last[p] = i;
                kept++;
            }
            Assert.True(dropped > 0, "nothing was dropped, so the flood never filled the queue");
            Assert.Equal(Producers * PerProducer, kept + dropped);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        for (var seen = Volatile.Read(ref target); value > seen; seen = Volatile.Read(ref target))
            if (Interlocked.CompareExchange(ref target, value, seen) == seen) return;
    }
}
