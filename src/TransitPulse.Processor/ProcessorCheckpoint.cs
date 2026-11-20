namespace TransitPulse.Processor;

internal static class ProcessorCheckpoint
{
    internal const int CurrentVersion = 1;

    public static ProcessorCheckpointSnapshot Capture(
        IReadOnlyDictionary<int, PartitionState> partitions,
        BunchingEventTimeBuffer bunching,
        IReadOnlyDictionary<int, HashSet<(string FeedId, string VehicleId)>> bunchingVehicles)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(bunching);
        ArgumentNullException.ThrowIfNull(bunchingVehicles);

        var savedPartitions = new ProcessorPartitionSnapshot[partitions.Count];
        var partitionIndex = 0;
        foreach (var (partition, state) in partitions)
        {
            if (partition < 0)
            {
                throw new InvalidOperationException($"Partition {partition} cannot be checkpointed.");
            }

            savedPartitions[partitionIndex++] = new ProcessorPartitionSnapshot(partition, state.Capture());
        }

        Array.Sort(savedPartitions, static (first, second) => first.Partition.CompareTo(second.Partition));
        var partitionById = savedPartitions.ToDictionary(saved => saved.Partition);

        var ownership = new ProcessorBunchingOwnershipSnapshot[bunchingVehicles.Count];
        var ownershipIndex = 0;
        var ownedVehicles = new HashSet<(string FeedId, string VehicleId)>();
        foreach (var (partition, vehicles) in bunchingVehicles)
        {
            if (!partitionById.TryGetValue(partition, out var savedPartition))
            {
                throw new InvalidOperationException($"Bunching ownership refers to unknown partition {partition}.");
            }

            var savedVehicles = new ProcessorVehicleKeySnapshot[vehicles.Count];
            var vehicleIndex = 0;
            foreach (var (feedId, vehicleId) in vehicles)
            {
                var vehicle = ValidateVehicle(feedId, vehicleId);
                if (!ownedVehicles.Add((vehicle.FeedId, vehicle.VehicleId))
                    || !savedPartition.State.Vehicles.Any(saved => saved.Key == $"{vehicle.FeedId}/{vehicle.VehicleId}"))
                {
                    throw new InvalidOperationException($"Vehicle {vehicle.FeedId}/{vehicle.VehicleId} has invalid partition ownership.");
                }

                savedVehicles[vehicleIndex++] = vehicle;
            }

            Array.Sort(savedVehicles, static (first, second) => CompareVehicles(first, second));
            ownership[ownershipIndex++] = new ProcessorBunchingOwnershipSnapshot(partition, savedVehicles);
        }

        Array.Sort(ownership, static (first, second) => first.Partition.CompareTo(second.Partition));
        var savedBunching = bunching.Capture();
        RequireDetectorOwnership(savedBunching, ownedVehicles);
        return new ProcessorCheckpointSnapshot(CurrentVersion, savedPartitions, savedBunching, ownership);
    }

    public static RestoredProcessorState Restore(ProcessorCheckpointSnapshot snapshot, CurrentTripSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (snapshot.Version != CurrentVersion || snapshot.Partitions is null || snapshot.BunchingOwnership is null)
        {
            throw new ArgumentException($"Unsupported or incomplete Processor checkpoint version {snapshot.Version}.", nameof(snapshot));
        }

        var currentSchedule = schedule.ReadCurrent();
        var bunching = BunchingEventTimeBuffer.Restore(snapshot.Bunching, currentSchedule, schedule.TimeZone);
        var partitions = new Dictionary<int, PartitionState>(snapshot.Partitions.Length);
        var partitionSnapshots = new Dictionary<int, PartitionStateSnapshot>(snapshot.Partitions.Length);
        foreach (var saved in snapshot.Partitions)
        {
            if (saved.Partition < 0
                || !partitionSnapshots.TryAdd(saved.Partition, saved.State)
                || !partitions.TryAdd(saved.Partition, PartitionState.Restore(saved.State, schedule, currentSchedule)))
            {
                throw new ArgumentException("The Processor checkpoint contains an invalid or duplicate partition.", nameof(snapshot));
            }
        }

        var ownership = new Dictionary<int, HashSet<(string FeedId, string VehicleId)>>(snapshot.BunchingOwnership.Length);
        var ownedVehicles = new HashSet<(string FeedId, string VehicleId)>();
        foreach (var saved in snapshot.BunchingOwnership)
        {
            if (saved.Vehicles is null
                || !partitionSnapshots.TryGetValue(saved.Partition, out var partition)
                || ownership.ContainsKey(saved.Partition))
            {
                throw new ArgumentException("The Processor checkpoint contains invalid bunching ownership.", nameof(snapshot));
            }

            var vehicles = new HashSet<(string FeedId, string VehicleId)>();
            foreach (var savedVehicle in saved.Vehicles)
            {
                var vehicle = ValidateVehicle(savedVehicle.FeedId, savedVehicle.VehicleId);
                if (!vehicles.Add((vehicle.FeedId, vehicle.VehicleId))
                    || !ownedVehicles.Add((vehicle.FeedId, vehicle.VehicleId))
                    || !partition.Vehicles.Any(item => item.Key == $"{vehicle.FeedId}/{vehicle.VehicleId}"))
                {
                    throw new ArgumentException("The Processor checkpoint contains invalid vehicle ownership.", nameof(snapshot));
                }
            }

            ownership.Add(saved.Partition, vehicles);
        }

        RequireDetectorOwnership(snapshot.Bunching, ownedVehicles);
        return new RestoredProcessorState(partitions, bunching, ownership);
    }

    public static RestoredProcessorState RestoreOwned(
        ProcessorCheckpointSnapshot snapshot,
        CurrentTripSchedule schedule,
        IReadOnlySet<int> ownedPartitions)
    {
        ArgumentNullException.ThrowIfNull(ownedPartitions);
        var restored = Restore(snapshot, schedule);
        if (restored.Partitions.Keys.Any(partition => !ownedPartitions.Contains(partition))
            || restored.BunchingVehicles.Keys.Any(partition => !ownedPartitions.Contains(partition))
            || snapshot.Bunching.Partitions.Any(partition => !ownedPartitions.Contains(partition.Partition)))
        {
            throw new InvalidOperationException(
                "The Processor checkpoint contains state for a partition not owned by this instance.");
        }

        return restored;
    }

    private static ProcessorVehicleKeySnapshot ValidateVehicle(string feedId, string vehicleId)
    {
        if (string.IsNullOrWhiteSpace(feedId) || string.IsNullOrWhiteSpace(vehicleId))
        {
            throw new ArgumentException("A bunching vehicle key is empty.");
        }

        return new ProcessorVehicleKeySnapshot(feedId, vehicleId);
    }

    private static void RequireDetectorOwnership(
        BunchingEventTimeBufferSnapshot bunching,
        HashSet<(string FeedId, string VehicleId)> ownedVehicles)
    {
        foreach (var line in bunching.Detector.Lines)
        {
            foreach (var observation in line.Vehicles)
            {
                var key = (observation.Position.FeedId, observation.Position.VehicleId);
                if (!ownedVehicles.Contains(key))
                {
                    throw new ArgumentException($"Detector vehicle {key.FeedId}/{key.VehicleId} has no partition ownership.");
                }
            }
        }
    }

    private static int CompareVehicles(ProcessorVehicleKeySnapshot first, ProcessorVehicleKeySnapshot second)
    {
        var feed = string.CompareOrdinal(first.FeedId, second.FeedId);
        return feed != 0 ? feed : string.CompareOrdinal(first.VehicleId, second.VehicleId);
    }
}

internal sealed record RestoredProcessorState(
    Dictionary<int, PartitionState> Partitions,
    BunchingEventTimeBuffer Bunching,
    Dictionary<int, HashSet<(string FeedId, string VehicleId)>> BunchingVehicles);

internal readonly record struct ProcessorCheckpointSnapshot(
    int Version,
    ProcessorPartitionSnapshot[] Partitions,
    BunchingEventTimeBufferSnapshot Bunching,
    ProcessorBunchingOwnershipSnapshot[] BunchingOwnership);

internal readonly record struct ProcessorPartitionSnapshot(int Partition, PartitionStateSnapshot State);

internal readonly record struct ProcessorBunchingOwnershipSnapshot(int Partition, ProcessorVehicleKeySnapshot[] Vehicles);

internal readonly record struct ProcessorVehicleKeySnapshot(string FeedId, string VehicleId);
