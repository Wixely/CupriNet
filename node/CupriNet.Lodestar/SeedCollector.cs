using Microsoft.Extensions.Logging;

namespace CupriNet.Lodestar;

/// <summary>
/// Gathers seed links from every supported source — the config array, a seeds file, the
/// <c>CUPRINET_LODESTAR_SEEDS</c> environment variable, and repeated <c>--seed</c> command-line arguments —
/// then trims, de-duplicates, and returns them. The goal is to take as many seeds as possible for a robust
/// first initialisation.
/// </summary>
internal static class SeedCollector
{
    private const string SeedsEnvVar = "CUPRINET_LODESTAR_SEEDS";
    private const string UriScheme = "cuprinet://";

    public static IReadOnlyList<string> Collect(LodestarOptions options, string[] commandLineArgs, ILogger logger)
    {
        var seeds = new List<string>();

        // 1. Config array (appsettings + CUPRINET_LODESTAR_SeedLinks__N).
        seeds.AddRange(options.SeedLinks);

        // 2. Seeds file: one link per line; blank lines and '#' comments ignored.
        if (!string.IsNullOrWhiteSpace(options.SeedsFile))
        {
            if (File.Exists(options.SeedsFile))
            {
                foreach (var raw in File.ReadLines(options.SeedsFile))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#'))
                        continue;
                    seeds.Add(line);
                }
            }
            else
            {
                logger.LogWarning("SeedsFile '{Path}' does not exist — skipping it.", options.SeedsFile);
            }
        }

        // 3. Environment variable: newline- / semicolon- / comma-separated links.
        var env = Environment.GetEnvironmentVariable(SeedsEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
            seeds.AddRange(env.Split(new[] { '\n', '\r', ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        // 4. Command line: --seed <link> (repeatable) or --seed=<link>.
        for (var i = 0; i < commandLineArgs.Length; i++)
        {
            var arg = commandLineArgs[i];
            if (arg.Equals("--seed", StringComparison.OrdinalIgnoreCase) && i + 1 < commandLineArgs.Length)
                seeds.Add(commandLineArgs[++i]);
            else if (arg.StartsWith("--seed=", StringComparison.OrdinalIgnoreCase))
                seeds.Add(arg["--seed=".Length..]);
        }

        // Normalise: trim, keep only plausible links, de-duplicate (case-sensitive — links are opaque payloads).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var seed in seeds)
        {
            var trimmed = seed.Trim();
            if (trimmed.Length == 0)
                continue;
            if (!trimmed.StartsWith(UriScheme, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Ignoring seed that is not a {Scheme} link: {Seed}", UriScheme, Truncate(trimmed));
                continue;
            }
            if (seen.Add(trimmed))
                result.Add(trimmed);
        }

        return result;
    }

    /// <summary>Shorten a link for log output — links are long and their tail is not needed to identify one.</summary>
    public static string Truncate(string link) => link.Length <= 48 ? link : link[..48] + "…";
}
