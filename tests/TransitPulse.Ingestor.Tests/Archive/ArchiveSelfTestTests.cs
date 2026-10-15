using TransitPulse.Ingestor.Archive;

namespace TransitPulse.Ingestor.Tests.Archive;

public sealed class ArchiveSelfTestTests
{
    // CI runs the self-test in the native binary; this keeps it passing under the JIT, so a red AOT job means AOT.
    [Fact]
    public async Task Self_test_passes()
    {
        Assert.Equal(0, await ArchiveSelfTest.RunAsync());
    }
}
