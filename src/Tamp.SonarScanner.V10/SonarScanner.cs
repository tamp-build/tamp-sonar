using System.Text.Json;
using System.Xml;

namespace Tamp.SonarScanner.V10;

/// <summary>
/// Wrapper for SonarScanner for .NET (the <c>dotnet-sonarscanner</c>
/// global tool, current major v10). Two-phase invocation: <see cref="Begin"/>
/// before the build, the build runs (where the scanner instruments
/// MSBuild), then <see cref="End"/> uploads results to the Sonar server.
/// </summary>
/// <remarks>
/// Resolve the tool via <see cref="NuGetPackageAttribute"/> on the build
/// class:
/// <code>
/// [NuGetPackage("dotnet-sonarscanner", Version = "10.0.0")] readonly Tool Sonar;
/// </code>
/// Or hand-construct a <see cref="Tool"/> if the binary is on a non-
/// standard path or system PATH.
/// <para>
/// For non-.NET projects (Java, JS, Python, generic), use the Java-CLI
/// sibling package <c>Tamp.SonarScannerCli.V6</c> instead — it has a
/// different invocation pattern (single-phase) and configuration model
/// (reads <c>sonar-project.properties</c> by default).
/// </para>
/// </remarks>
public static class SonarScanner
{
    /// <summary>Sonar properties that SonarQube Community Edition rejects with "Developer Edition or above is required".</summary>
    internal static readonly string[] CommunityEditionForbiddenProperties =
    {
        "sonar.branch.name",
        "sonar.branch.target",
    };

    /// <summary>Build the <c>begin</c> phase command plan.</summary>
    public static CommandPlan Begin(Tool tool, Action<SonarBeginSettings> configure)
    {
        if (tool is null) throw new ArgumentNullException(nameof(tool));
        if (configure is null) throw new ArgumentNullException(nameof(configure));

        var s = new SonarBeginSettings();
        configure(s);
        return s.ToCommandPlan(tool);
    }

    /// <summary>Build the <c>end</c> phase command plan.</summary>
    public static CommandPlan End(Tool tool, Action<SonarEndSettings>? configure = null)
    {
        if (tool is null) throw new ArgumentNullException(nameof(tool));
        var s = new SonarEndSettings();
        configure?.Invoke(s);
        return s.ToCommandPlan(tool);
    }

    /// <summary>
    /// Strip <c>sonar.branch.name</c> and <c>sonar.branch.target</c> from
    /// the <c>SonarQubeAnalysisConfig.xml</c> file the .NET scanner
    /// writes during <see cref="Begin"/>.
    ///
    /// <para>Necessary for SonarQube Community Edition pipelines where
    /// the ADO SonarSource extension auto-injects these properties via
    /// MSBuild XML, bypassing the env-var cleanup that
    /// <see cref="SonarBeginSettings.CommunityEdition"/> handles. Call
    /// from a target that runs between Begin and Compile.</para>
    /// </summary>
    /// <param name="sonarqubeDirectoryOrConfigPath">Either the project's <c>.sonarqube/</c> directory (the wrapper walks it to find every <c>SonarQubeAnalysisConfig.xml</c>) or a direct path to the XML file.</param>
    /// <returns>The number of property entries removed across all files modified.</returns>
    public static int StripBranchPropertiesFromAnalysisConfigXml(string sonarqubeDirectoryOrConfigPath)
    {
        if (string.IsNullOrEmpty(sonarqubeDirectoryOrConfigPath))
            throw new ArgumentException("Path is required.", nameof(sonarqubeDirectoryOrConfigPath));

        var files = ResolveAnalysisConfigPaths(sonarqubeDirectoryOrConfigPath).ToList();
        var removed = 0;
        foreach (var file in files)
            removed += StripBranchPropertiesFromXml(file);
        return removed;
    }

    private static IEnumerable<string> ResolveAnalysisConfigPaths(string path)
    {
        if (File.Exists(path))
        {
            yield return path;
            yield break;
        }
        if (Directory.Exists(path))
        {
            foreach (var f in Directory.EnumerateFiles(path, "SonarQubeAnalysisConfig.xml", SearchOption.AllDirectories))
                yield return f;
            yield break;
        }
        // Path doesn't exist as either file or directory — silently no-op.
        // (Tests construct paths before Begin has run.)
    }

    private static int StripBranchPropertiesFromXml(string path)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(path);

        // The scanner emits properties as either:
        //   <Property Name="sonar.branch.name">main</Property>
        // or:
        //   <Setting Id="sonar.branch.name"><Value>main</Value></Setting>
        // We strip both shapes from anywhere in the document.
        var removed = 0;
        foreach (var name in CommunityEditionForbiddenProperties)
        {
            removed += RemoveNodesByAttribute(doc, "Name", name);
            removed += RemoveNodesByAttribute(doc, "Id", name);
        }

        if (removed > 0) doc.Save(path);
        return removed;
    }

    private static int RemoveNodesByAttribute(XmlDocument doc, string attrName, string attrValue)
    {
        var matches = doc.SelectNodes($"//*[@{attrName}='{attrValue}']");
        if (matches is null) return 0;
        var snapshot = matches.Cast<XmlNode>().ToList();
        var count = 0;
        foreach (var node in snapshot)
        {
            node.ParentNode?.RemoveChild(node);
            count++;
        }
        return count;
    }

    internal static void ApplyCommunityEditionEnvCleanup(Dictionary<string, string> env)
    {
        // The ADO SonarSource extension (SonarQubePrepare@8) writes a
        // JSON blob to SONARQUBE_SCANNER_PARAMS during its task setup.
        // Anything that subsequently spawns and inherits the env reads
        // that JSON. CE chokes on the branch properties inside.
        //
        // Read the inherited value at plan-construction time, strip the
        // forbidden keys, emit the cleaned JSON into the plan's env —
        // which overrides the inherited value for the subprocess.
        const string EnvVar = "SONARQUBE_SCANNER_PARAMS";
        var source = env.TryGetValue(EnvVar, out var explicitValue)
            ? explicitValue
            : Environment.GetEnvironmentVariable(EnvVar);

        if (string.IsNullOrEmpty(source))
        {
            // Neither explicitly set nor inherited — emit an empty
            // object as a defensive override so anything that injects
            // the var later in the process can't trip the gate.
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
            // SONARQUBE_SCANNER_PARAMS wasn't valid JSON. Leave it
            // alone — the scanner will reject it on its own terms.
        }
    }

    // ---- Object-init overloads (0.3.1+, TAM-161) ----
    // Two equivalent authoring styles; both produce identical CommandPlans. Fluent
    // stays canonical in docs and `tamp init` templates; object-init available for
    // consumers who prefer the C# initializer shape.
    //
    //     SonarScanner.Begin(Sonar, new() { ProjectKey = "acme:lib", HostUrl = "https://sq" });
    //
    // is equivalent to:
    //
    //     SonarScanner.Begin(Sonar, s => s.SetProjectKey("acme:lib").SetHostUrl("https://sq"));

    public static CommandPlan Begin(Tool tool, SonarBeginSettings settings) => settings.ToCommandPlan(tool);
    public static CommandPlan End(Tool tool, SonarEndSettings settings) => settings.ToCommandPlan(tool);
}
