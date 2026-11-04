namespace TransitPulse.Domain;

/// <summary>What <see cref="VehicleTrack.Check"/> decided about a position.</summary>
public enum PositionCheck
{
    /// <summary>The position is plausible and may be processed.</summary>
    Accepted,

    /// <summary>The position is not newer than the vehicle's last one.</summary>
    OutOfOrder,

    /// <summary>Reaching the position would take an impossible speed.</summary>
    ImpossibleJump,
}
