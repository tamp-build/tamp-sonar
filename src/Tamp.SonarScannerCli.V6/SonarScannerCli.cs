using System.Text.Json;

namespace Tamp.SonarScannerCli.V6;

/// <summary>
/// Wrapper for the SonarScanner CLI — the Java <c>sonar-scanner</c>
/// binary, current major v6. Single-phase invocation. Reads
/// <c>sonar-project.properties</c> from the working directory by
/// default; command-line <c>-D</c> flags override anything in the
/// file.
/// </summary>
/// <remarks>
/// The Java CLI is distributed as a zip from Sonar; install it however
/// suits the runner (manual extract, package manager, container image,
/// pre-staged on the runner) and resolve the <see cref="Tool"/> via
/// <c>[NuGetPackage(UseSystemPath = true, ExecutableName = "sonar-scanner")]</c>
/// or by hand-constructing one from a configured path.
/// <para>
/// For .NET / MSBuild projects, use the sibling package
/// <c>Tamp.SonarScanner.V10</c> instead — different tool with
/// two-phase begin/end invocation.
/// </para>
/// </remarks>
public static class SonarScannerCli
{
    /// <summary>Sonar properties that SonarQube Community Edition rejects with "Developer Edition or above is required".</summary>
    internal static readonly string[] CommunityEditionForbiddenProperties =
    {
        "sonar.branch.name",
        "sonar.branch.target",
    };

    /// <summary>
    /// Build a <c>sonar-scanner</c> command plan. Pass an empty
    /// configurer (or <c>null</c>) to run with whatever's in the
    /// working directory's <c>sonar-project.properties</c>; supply
    /// settings to override individual properties on the command
    /// line.
    /// </summary>
    public static CommandPlan Scan(Tool tool, Action<SonarScanSettings>? configure = null)
    {
        if (tool is null) throw new ArgumentNullException(nameof(tool));
        var s = new SonarScanSettings();
        configure?.Invoke(s);

        var args = new List<string>();
        if (!string.IsNullOrEmpty(s.ProjectKey)) args.Add($"-Dsonar.projectKey={s.ProjectKey}");
        if (!string.IsNullOrEmpty(s.HostUrl)) args.Add($"-Dsonar.host.url={s.HostUrl}");
        // TODO: extract Reveal into SonarScannerCliTokenSettings to satisfy TAMP004 cleanly.
#pragma warning disable TAMP004
        if (s.Token is { } t) args.Add($"-Dsonar.token={t.Reveal()}");
#pragma warning restore TAMP004
        if (!string.IsNullOrEmpty(s.Organization)) args.Add($"-Dsonar.organization={s.Organization}");
        if (!string.IsNullOrEmpty(s.ProjectBaseDir)) args.Add($"-Dsonar.projectBaseDir={s.ProjectBaseDir}");
        if (!string.IsNullOrEmpty(s.Sources)) args.Add($"-Dsonar.sources={s.Sources}");
        if (!string.IsNullOrEmpty(s.PropertiesFile)) args.Add($"-Dproject.settings={s.PropertiesFile}");
        foreach (var (k, v) in s.AdditionalProperties)
        {
            // Belt-and-braces: if the caller set CommunityEdition AND
            // a forbidden property, drop the property silently.
            if (s.CommunityEdition && CommunityEditionForbiddenProperties.Contains(k)) continue;
            args.Add($"-D{k}={v}");
        }

        var env = new Dictionary<string, string>(s.EnvironmentVariables);
        if (s.CommunityEdition)
            ApplyCommunityEditionEnvCleanup(env);

        return new CommandPlan
        {
            Executable = tool.Executable.Value,
            Arguments = args,
            Environment = env,
            WorkingDirectory = s.WorkingDirectory ?? tool.WorkingDirectory,
            Secrets = s.Token is null ? Array.Empty<Secret>() : new[] { s.Token },
        };
    }

    private static void ApplyCommunityEditionEnvCleanup(Dictionary<string, string> env)
    {
        // The ADO SonarSource extension (SonarQubePrepare@8) writes a
        // JSON blob to SONARQUBE_SCANNER_PARAMS during its task setup.
        // The Java CLI scanner reads this env var the same way the .NET
        // scanner does, so CE chokes the same way.
        const string EnvVar = "SONARQUBE_SCANNER_PARAMS";
        var source = env.TryGetValue(EnvVar, out var explicitValue)
            ? explicitValue
            : Environment.GetEnvironmentVariable(EnvVar);

        if (string.IsNullOrEmpty(source))
        {
            env[EnvVar] = "{}";
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(source);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

            var cleaned = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (CommunityEditionForbiddenProperties.Contains(prop.Name)) continue;
                if (prop.Value.ValueKind == JsonValueKind.String)
                    cleaned[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
            env[EnvVar] = JsonSerializer.Serialize(cleaned);
        }
        catch (JsonException)
        {
            // Inherited env wasn't valid JSON. Leave it alone — the
            // scanner will reject it on its own terms.
        }
    }
}
