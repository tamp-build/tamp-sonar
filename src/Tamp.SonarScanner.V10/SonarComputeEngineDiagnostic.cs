using Tamp;

namespace Tamp.SonarScanner.V10;

/// <summary>
/// Result of inspecting a Sonar scanner End-phase (Publish) failure for the compute-engine
/// stall signature. Returned by <see cref="SonarComputeEngineDiagnostic.Inspect(int, string, string)"/>.
/// </summary>
/// <param name="ComputeEngineStallDetected">True when the captured output matched a known stall signature.</param>
/// <param name="MatchedSignature">The first signature substring matched, for log forensics. Null when nothing matched.</param>
/// <param name="ActionableHint">A multi-line hint suitable for printing to a CI log when a stall is detected. Null when nothing matched.</param>
public sealed record SonarPublishDiagnostic(
    bool ComputeEngineStallDetected,
    string? MatchedSignature,
    string? ActionableHint)
{
    /// <summary>Singleton "all clear" verdict — returned when no stall signature was found.</summary>
    public static readonly SonarPublishDiagnostic NoStallDetected = new(false, null, null);
}

/// <summary>
/// Detects the SonarQube Community Edition compute-engine stall pattern in scanner End-phase output.
/// </summary>
/// <remarks>
/// <para>
/// SonarQube CE periodically wedges its compute engine with an
/// <c>IllegalArgumentException: Execution time must be positive: -NNN</c> in <c>CeWorkerImpl.finalize</c>
/// — a clock-arithmetic glitch where a finished task records a negative duration. The scanner client
/// sees the analysis task transition to <c>CANCELED</c> and exits non-zero, but the message it prints
/// is generic and easy to miss in a long CI log. The "fix" is <c>docker restart &lt;sonarqube&gt;</c>
/// and re-run.
/// </para>
/// <para>
/// This diagnostic inspects the scanner's stdout / stderr for the client-visible markers ("Task failed
/// with status CANCELED" / "ANALYSIS_REPORT.+CANCELED") and returns a typed verdict so build scripts
/// can either:
/// </para>
/// <list type="bullet">
///   <item>Emit a more actionable error than the raw scanner output, OR</item>
///   <item>Conditionally retry — wrap the analysis target in a retry policy that only retries on
///         <c>ComputeEngineStallDetected = true</c>.</item>
/// </list>
/// <para>
/// Detection is best-effort. The actual stack trace lives in the SQ server's compute-engine log, not
/// the scanner client's output — this helper does NOT poll the SQ API for the task's real status.
/// False negatives are possible if SonarQube changes its CLI messaging; false positives are unlikely
/// because the "CANCELED" status string is specific to the compute engine.
/// </para>
/// </remarks>
public static class SonarComputeEngineDiagnostic
{
    // Signatures observed in End-phase output when CeWorkerImpl finalises with a negative duration.
    // Ordered longest-first so more-specific literals win over their substrings
    // ("Background task failed with status CANCELED" is a superset of
    // "Task failed with status CANCELED").
    internal static readonly string[] StallSignatures =
    [
        "Background task failed with status CANCELED",
        "Task failed with status CANCELED",
        "ANALYSIS_REPORT.*CANCELED", // looser — paired with regex match below
    ];

    private const string Hint =
        "Sonar Publish failed with no Quality Gate outcome. This often indicates the SonarQube\n" +
        "Community Edition compute engine has stalled. Try:\n" +
        "  docker restart <sonarqube-container>\n" +
        "and re-run.\n" +
        "Known SQ CE bug: IllegalArgumentException 'Execution time must be positive' in\n" +
        "CeWorkerImpl.finalize — clock-arithmetic glitch. Check the SonarQube container log\n" +
        "for the full stack trace.";

    /// <summary>
    /// Inspects an End-phase result for the compute-engine stall signature.
    /// </summary>
    /// <param name="exitCode">Exit code of the <c>dotnet-sonarscanner end</c> invocation.</param>
    /// <param name="stdOut">Captured stdout (may be empty).</param>
    /// <param name="stdErr">Captured stderr (may be empty).</param>
    public static SonarPublishDiagnostic Inspect(int exitCode, string stdOut, string stdErr)
    {
        if (exitCode == 0) return SonarPublishDiagnostic.NoStallDetected;

        var haystack = (stdOut ?? "") + "\n" + (stdErr ?? "");
        foreach (var signature in StallSignatures)
        {
            if (signature.Contains(".*", StringComparison.Ordinal))
            {
                // Regex-form signature (very narrow — just supports `.*` as wildcard between two literals).
                var parts = signature.Split(".*", 2);
                var firstIdx = haystack.IndexOf(parts[0], StringComparison.OrdinalIgnoreCase);
                if (firstIdx < 0) continue;
                var secondIdx = haystack.IndexOf(parts[1], firstIdx + parts[0].Length, StringComparison.OrdinalIgnoreCase);
                if (secondIdx < 0) continue;
                return new SonarPublishDiagnostic(true, signature, Hint);
            }
            if (haystack.Contains(signature, StringComparison.OrdinalIgnoreCase))
                return new SonarPublishDiagnostic(true, signature, Hint);
        }

        return SonarPublishDiagnostic.NoStallDetected;
    }

    /// <summary>
    /// Inspects a <see cref="CaptureResult"/> from <c>ProcessRunner.Capture</c> wrapping a
    /// <see cref="SonarScanner.End"/> CommandPlan.
    /// </summary>
    public static SonarPublishDiagnostic Inspect(CaptureResult endResult)
    {
        if (endResult is null) throw new ArgumentNullException(nameof(endResult));
        return Inspect(endResult.ExitCode, endResult.StdoutText, endResult.StderrText);
    }
}
