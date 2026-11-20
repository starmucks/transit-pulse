using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TransitPulse.Processor.Kafka;

namespace TransitPulse.Processor;

internal interface IProcessorCheckpointStore
{
    ProcessorCheckpointSnapshot? Load();

    void Save(ProcessorCheckpointSnapshot snapshot);
}

internal sealed class ProcessorCheckpointStore(ICheckpointBlobStore blobs) : IProcessorCheckpointStore
{
    public ProcessorCheckpointSnapshot? Load()
    {
        var payload = blobs.Load();
        return payload is null ? null : ProcessorCheckpointSerializer.Deserialize(payload);
    }

    public void Save(ProcessorCheckpointSnapshot snapshot) =>
        blobs.Save(ProcessorCheckpointSerializer.Serialize(snapshot));
}

internal interface ICheckpointBlobStore
{
    byte[]? Load();

    void Save(byte[] payload);
}

internal sealed class RedisCheckpointBlobStore : ICheckpointBlobStore
{
    private const string KeyPrefix = "transitpulse:processor:checkpoint";
    private const int PersistenceTimeoutMilliseconds = 5_000;

    private readonly IDatabase _database;
    private readonly RedisKey _key;

    public RedisCheckpointBlobStore(
        IConnectionMultiplexer connection,
        IOptions<ProcessorKafkaOptions> options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        var kafka = options.Value;
        if (string.IsNullOrWhiteSpace(kafka.GroupId) || string.IsNullOrWhiteSpace(kafka.InputTopic))
        {
            throw new ArgumentException("Kafka group id and input topic are required for the checkpoint key.", nameof(options));
        }

        _database = connection.GetDatabase();
        _key = $"{KeyPrefix}:{kafka.GroupId}:{kafka.InputTopic}";
    }

    public byte[]? Load()
    {
        var value = _database.StringGet(_key);
        return value.IsNull ? null : (byte[]?)value;
    }

    public void Save(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!_database.StringSet(_key, payload))
        {
            throw new InvalidOperationException("Redis did not store the Processor checkpoint.");
        }

        var persisted = (RedisResult[]?)_database.Execute(
            "WAITAOF",
            1,
            0,
            PersistenceTimeoutMilliseconds);
        if (persisted is null || persisted.Length != 2 || (long)persisted[0] < 1)
        {
            throw new TimeoutException("Redis did not fsync the Processor checkpoint before the persistence timeout.");
        }
    }
}
