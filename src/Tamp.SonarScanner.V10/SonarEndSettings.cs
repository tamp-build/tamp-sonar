namespace Tamp.SonarScanner.V10;

/// <summary>
/// Settings for the <c>end</c> phase of SonarScanner for .NET. Almost
/// always a one-liner: pass the same token used in <c>begin</c>. The
/// scanner reads its remaining state from the <c>.sonarqube/</c>
/// directory <c>begin</c> wrote to disk.
/// </summary>
public sealed class SonarEndSettings
{
    /// <summary>Authentication token (same one used in begin).</summary>
    public Secret? Token { get; set; }

    /// <summary>Free-form additional properties (rare for end-phase).</summary>
    public Dictionary<string, string> AdditionalProperties { get; } = new();

    /// <summary>Working directory for the spawned process. Should match the begin phase's working directory.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Additional environment variables.</summary>
    public Dictionary<string, string> EnvironmentVariables { get; } = new();

    public SonarEndSettings SetToken(Secret token) { Token = token; return this; }
    public SonarEndSettings SetProperty(string name, string value) { AdditionalProperties[name] = value; return this; }
    public SonarEndSettings SetWorkingDirectory(string? cwd) { WorkingDirectory = cwd; return this; }

    /// <summary>
    /// Materialise this settings object into a <see cref="CommandPlan"/> for the end phase.
    /// Powers both the fluent <see cref="SonarScanner.End(Tool, Action{SonarEndSettings}?)"/>
    /// overload and the object-init <see cref="SonarScanner.End(Tool, SonarEndSettings)"/>
    /// overload — identical output for either authoring style.
    /// </summary>
    internal CommandPlan ToCommandPlan(Tool tool)
    {
        if (tool is null) throw new ArgumentNullException(nameof(tool));

        var args = new List<string> { "end" };
        if (Token is { } t) args.Add($"/d:sonar.token={t.Reveal()}");
        foreach (var (k, v) in AdditionalProperties)
            args.Add($"/d:{k}={v}");

        return new CommandPlan
        {
            Executable = tool.Executable.Value,
            Arguments = args,
            Environment = new Dictionary<string, string>(EnvironmentVariables),
            WorkingDirectory = WorkingDirectory ?? tool.WorkingDirectory,
            Secrets = Token is null ? Array.Empty<Secret>() : new[] { Token },
        };
    }
}
