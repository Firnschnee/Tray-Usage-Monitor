using Xunit;

namespace ClaudeUsageMonitor.Tests;

public class SmokeTests
{
    [Fact]
    public void MainAssemblyIsReferenced()
    {
        var data = new UsageData { SessionPercent = 42 };
        Assert.Equal(42, data.SessionPercent);
    }
}
