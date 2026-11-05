namespace TransitPulse.Domain.Tests;

public sealed class VehicleTrackTests
{
    private const double MetresPerDegreeOfLatitude = 111_194.93;
    private const double StartLat = 60.17;
    private const double Lon = 24.94;

    private static readonly DateTimeOffset _start = new(2026, 10, 27, 14, 0, 0, TimeSpan.Zero);

    private readonly VehicleTrack _track = new();

    /// <summary>A position the given number of metres north of the start, at the given second.</summary>
    private static VehiclePosition At(double seconds, double metresNorth = 0) => new(
        "hsl", "22/1435", null, null, StartLat + (metresNorth / MetresPerDegreeOfLatitude), Lon, null, null, null, null, _start.AddSeconds(seconds));

    private PositionCheck Check(double seconds, double metresNorth = 0) => _track.Check(At(seconds, metresNorth));

    [Fact]
    public void First_position_is_accepted()
    {
        Assert.Equal(PositionCheck.Accepted, Check(0));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(9)]
    public void Position_not_newer_than_the_last_one_is_out_of_order(double seconds)
    {
        Check(10);

        Assert.Equal(PositionCheck.OutOfOrder, Check(seconds));
    }

    [Fact]
    public void Out_of_order_position_does_not_change_the_track()
    {
        Check(10);
        Check(5, metresNorth: 5_000);

        Assert.Equal(PositionCheck.Accepted, Check(11, metresNorth: 10));
    }

    [Fact]
    public void Normal_movement_is_accepted()
    {
        Check(0);

        Assert.Equal(PositionCheck.Accepted, Check(1, metresNorth: 20));
        Assert.Equal(PositionCheck.Accepted, Check(2, metresNorth: 40));
    }

    [Theory]
    [InlineData(49, PositionCheck.ImpossibleJump)] // 61 m/s
    [InlineData(51, PositionCheck.Accepted)] // 59 m/s
    public void Speed_decides_over_a_long_distance(double seconds, PositionCheck expected)
    {
        Check(0);

        Assert.Equal(expected, Check(seconds, metresNorth: 3_000));
    }

    [Theory]
    [InlineData(199, PositionCheck.Accepted)]
    [InlineData(201, PositionCheck.ImpossibleJump)]
    public void Distance_decides_over_a_fraction_of_a_second(double metres, PositionCheck expected)
    {
        Check(0);

        Assert.Equal(expected, Check(0.1, metresNorth: metres));
    }

    [Fact]
    public void Repeats_of_known_coordinates_do_not_make_the_next_fix_look_fast()
    {
        // HSL metro: 250 m in 5 s is 50 m/s, but only 250 m/s from the last repeat.
        Check(0);
        for (var second = 1; second <= 4; second++)
        {
            Assert.Equal(PositionCheck.Accepted, Check(second));
        }

        Assert.Equal(PositionCheck.Accepted, Check(5, metresNorth: 250));
    }

    [Fact]
    public void One_point_glitch_loses_only_that_point()
    {
        Check(0);

        Assert.Equal(PositionCheck.ImpossibleJump, Check(1, metresNorth: 5_000));
        Assert.Equal(PositionCheck.Accepted, Check(2, metresNorth: 20));
    }

    [Fact]
    public void A_glitch_is_forgotten_once_the_vehicle_is_back()
    {
        Check(0);
        Check(1, metresNorth: 5_000);
        Check(2, metresNorth: 20);

        // Near the old glitch, but it no longer counts as a place set aside.
        Assert.Equal(PositionCheck.ImpossibleJump, Check(3, metresNorth: 5_010));
    }

    [Fact]
    public void Lasting_move_is_followed_after_one_lost_point()
    {
        Check(0);

        Assert.Equal(PositionCheck.ImpossibleJump, Check(1, metresNorth: 5_000));
        Assert.Equal(PositionCheck.Accepted, Check(2, metresNorth: 5_020));
        Assert.Equal(PositionCheck.Accepted, Check(3, metresNorth: 5_040));
    }

    [Fact]
    public void After_a_lasting_move_the_old_place_is_a_jump()
    {
        Check(0);
        Check(1, metresNorth: 5_000);
        Check(2, metresNorth: 5_020);

        Assert.Equal(PositionCheck.ImpossibleJump, Check(3));
    }

    [Fact]
    public void Lasting_move_confirmed_by_a_repeat_keeps_the_time_of_the_first_report()
    {
        Check(0);
        Check(1, metresNorth: 5_000);
        Assert.Equal(PositionCheck.Accepted, Check(5, metresNorth: 5_000));

        // 250 m is 50 m/s from the first report at 1 s, but 250 m/s from the repeat at 5 s.
        Assert.Equal(PositionCheck.Accepted, Check(6, metresNorth: 5_250));
    }

    [Fact]
    public void Two_different_outliers_in_a_row_are_both_dropped_and_the_last_one_counts()
    {
        Check(0);

        Assert.Equal(PositionCheck.ImpossibleJump, Check(1, metresNorth: 5_000));
        Assert.Equal(PositionCheck.ImpossibleJump, Check(2, metresNorth: 20_000));
        Assert.Equal(PositionCheck.Accepted, Check(3, metresNorth: 20_020));
    }

    [Fact]
    public void Time_alone_makes_a_far_position_plausible_again()
    {
        Check(0);
        Check(1, metresNorth: 60_000);

        // 59 km in 1 001 s is under 60 m/s from the old place, so the point set aside is not needed.
        Assert.Equal(PositionCheck.Accepted, Check(1_001, metresNorth: 59_000));
    }

    [Fact]
    public void Null_position_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => _track.Check(null!));
    }
}
