using TransitPulse.Ingestor.Archive;
using TransitPulse.Ingestor.Normalization;
using TransitPulse.Ingestor.Pipeline;
using TransitPulse.Ingestor.Replay;

// Checked before the host is built: the self-test needs no configuration, telemetry or feeds.
if (args.Contains("--self-test"))
{
    return await ArchiveSelfTest.RunAsync();
}

if (args is ["--repair", var repairInput, var repairOutput])
{
    return await ArchiveRepair.RunAsync(repairInput, repairOutput, Console.Out);
}

if (args is ["--stats", var statsInput])
{
    return await ArchiveStats.RunAsync(statsInput, Console.Out);
}

if (await ArchiveProfileCli.TryRunAsync(args, Console.Out, Console.Error) is { } profileExit)
{
    return profileExit;
}

var builder = Host.CreateApplicationBuilder(args);
var mode = IngestorHost.Configure(builder);

using var host = builder.Build();

// Resolved before running: the host disposes its services when it stops. A failed replay stops the host like a
// finished one, so only its flag tells the two apart.
var replay = mode == IngestorMode.Replay ? host.Services.GetRequiredService<ReplaySource>() : null;
var exitCode = await IngestionPipeline.RunHostAsync(host);
return replay is { Failed: true } ? 1 : exitCode;
