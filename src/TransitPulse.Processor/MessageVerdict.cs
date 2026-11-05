namespace TransitPulse.Processor;

/// <summary>What <see cref="PartitionState.Process"/> decided about a message.</summary>
internal enum MessageVerdict
{
    /// <summary>The position goes on to the next topic.</summary>
    Accepted,

    /// <summary>Dropped: not newer than the vehicle's last position.</summary>
    OutOfOrder,

    /// <summary>Dropped: reaching the position would take an impossible speed.</summary>
    ImpossibleJump,

    /// <summary>Dropped: the message has no key or does not hold a valid position.</summary>
    Unreadable,
}
