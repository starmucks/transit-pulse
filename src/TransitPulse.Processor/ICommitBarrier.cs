namespace TransitPulse.Processor;

/// <summary>
/// Test seam for the crash window after the Redis checkpoint is durable and before Kafka offsets are committed
/// (ADR-0014). Production uses <see cref="PassThroughCommitBarrier"/>.
/// </summary>
internal interface ICommitBarrier
{
    /// <summary>Called after a successful checkpoint save and before the Kafka offset commit.</summary>
    void AfterCheckpointSaved();
}

/// <summary>Production barrier: never interrupts the commit path.</summary>
internal sealed class PassThroughCommitBarrier : ICommitBarrier
{
    public void AfterCheckpointSaved()
    {
    }
}
