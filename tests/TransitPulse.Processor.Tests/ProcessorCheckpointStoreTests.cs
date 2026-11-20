using System.Text.Json;

namespace TransitPulse.Processor.Tests;

public sealed class ProcessorCheckpointStoreTests
{
    [Fact]
    public void Missing_checkpoint_returns_null()
    {
        var store = new ProcessorCheckpointStore(new MemoryCheckpointBlobStore());

        Assert.Null(store.Load());
    }

    [Fact]
    public void Saved_checkpoint_round_trips_through_blob_storage()
    {
        var blobs = new MemoryCheckpointBlobStore();
        var store = new ProcessorCheckpointStore(blobs);
        var snapshot = EmptySnapshot();

        store.Save(snapshot);
        var loaded = store.Load();

        Assert.True(loaded.HasValue);
        Assert.Equal(
            ProcessorCheckpointSerializer.Serialize(snapshot),
            ProcessorCheckpointSerializer.Serialize(loaded.Value));
    }

    [Fact]
    public void Corrupt_checkpoint_is_rejected()
    {
        var blobs = new MemoryCheckpointBlobStore { Value = "{not-json"u8.ToArray() };
        var store = new ProcessorCheckpointStore(blobs);

        Assert.Throws<JsonException>(() => store.Load());
    }

    [Fact]
    public void Storage_failure_is_not_hidden()
    {
        var failure = new InvalidOperationException("storage unavailable");
        var store = new ProcessorCheckpointStore(new MemoryCheckpointBlobStore { SaveFailure = failure });

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => store.Save(EmptySnapshot())));
    }

    private static ProcessorCheckpointSnapshot EmptySnapshot() => ProcessorCheckpoint.Capture(
        new Dictionary<int, PartitionState>(),
        new BunchingEventTimeBuffer(TimeZoneInfo.Utc),
        new Dictionary<int, HashSet<(string FeedId, string VehicleId)>>());

    private sealed class MemoryCheckpointBlobStore : ICheckpointBlobStore
    {
        public byte[]? Value { get; set; }

        public Exception? SaveFailure { get; init; }

        public byte[]? Load() => Value;

        public void Save(byte[] payload)
        {
            if (SaveFailure is not null)
            {
                throw SaveFailure;
            }

            Value = payload;
        }
    }
}
