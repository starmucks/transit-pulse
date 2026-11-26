using TransitPulse.Api.Map;
using TransitPulse.Infrastructure.Redis;

namespace TransitPulse.Api.Tests;

public sealed class MapViewportLimitsTests
{
    [Fact]
    public void ResolveBox_rejects_missing_bbox_and_route()
    {
        var ex = Assert.Throws<ArgumentException>(() => MapViewportLimits.ResolveBox(null, null));
        Assert.Contains("bounding box", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveBox_rejects_oversized_bbox()
    {
        var box = new GeoBoundingBox(24.0, 60.0, 25.0, 60.2);
        var ex = Assert.Throws<ArgumentException>(() => MapViewportLimits.ResolveBox(box, null));
        Assert.Contains("span must be at most", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveBox_uses_helsinki_cover_for_route_only()
    {
        var box = MapViewportLimits.ResolveBox(null, "1009");
        Assert.Equal(MapViewportLimits.HelsinkiCoveringBox, box);
    }

    [Fact]
    public void ResolveBox_keeps_valid_bbox()
    {
        var box = new GeoBoundingBox(24.9, 60.15, 25.0, 60.2);
        Assert.Equal(box, MapViewportLimits.ResolveBox(box, "1009"));
    }
}
