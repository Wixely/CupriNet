using CupriNet.Lodestar;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// A Lodestar is a headless "keep the network alive" node. It runs happily as:
//   • a plain console app (Ctrl+C to stop) on Windows or Ubuntu,
//   • a Windows Service (UseWindowsService),
//   • a Linux systemd daemon (UseSystemd),
//   • a Docker container (runs in the foreground; logs to stdout).
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // A Windows service starts with its working directory in System32, so pin the content root to the exe's folder
    // — where appsettings.json lives — so the service, a console run, and systemd all read the same config file.
    ContentRootPath = AppContext.BaseDirectory,
});

// Environment overrides: CUPRINET_LODESTAR_Concordium, CUPRINET_LODESTAR_ListenPort, CUPRINET_LODESTAR_SeedLinks__0, …
// The prefix is stripped, so these land at the configuration root (bound below alongside the appsettings section).
builder.Configuration.AddEnvironmentVariables(prefix: "CUPRINET_LODESTAR_");

builder.Services
    .AddOptions<LodestarOptions>()
    .Bind(builder.Configuration.GetSection(LodestarOptions.SectionName)) // appsettings.json "Lodestar" section
    .Bind(builder.Configuration)                                        // CUPRINET_LODESTAR_* env (+ --key) at root
    .ValidateOnStart();

builder.Services.AddHostedService<LodestarService>();

// Integrate with the host manager so `systemctl`/`sc` see correct ready/stopping states and log formatting.
builder.Services.AddWindowsService(o => o.ServiceName = "CupriNet Lodestar");
builder.Services.AddSystemd();

// A daemon wants simple, timestamped, single-line logs; the host manager adds its own framing where relevant.
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

await builder.Build().RunAsync();
