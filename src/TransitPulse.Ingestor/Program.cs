var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

using var host = builder.Build();
await host.RunAsync();
