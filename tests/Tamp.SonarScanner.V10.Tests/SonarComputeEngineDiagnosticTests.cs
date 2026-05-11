using Xunit;

namespace Tamp.SonarScanner.V10.Tests;

public sealed class SonarComputeEngineDiagnosticTests
{
    // ---- Negative-result paths: no stall detected ----

    [Fact]
    public void ExitCode_Zero_Returns_NoStallDetected_Regardless_Of_Output()
    {
        // Even if the CANCELED marker is in the output, exit 0 means the analysis
        // actually succeeded — the wrapper trusts the exit code as the primary signal.
        var diag = SonarComputeEngineDiagnostic.Inspect(0, "Task failed with status CANCELED", "");
        Assert.False(diag.ComputeEngineStallDetected);
        Assert.Null(diag.ActionableHint);
        Assert.Null(diag.MatchedSignature);
        Assert.Same(SonarPublishDiagnostic.NoStallDetected, diag);
    }

    [Fact]
    public void Generic_NonZero_Exit_Without_Marker_Returns_NoStallDetected()
    {
        var diag = SonarComputeEngineDiagnostic.Inspect(
            exitCode: 1,
            stdOut: "Compilation errors found. Stopping analysis.",
            stdErr: "");
        Assert.False(diag.ComputeEngineStallDetected);
        Assert.Null(diag.MatchedSignature);
    }

    [Fact]
    public void Empty_Output_Returns_NoStallDetected()
    {
        var diag = SonarComputeEngineDiagnostic.Inspect(1, "", "");
        Assert.False(diag.ComputeEngineStallDetected);
    }

    // ---- Positive-result paths: stall detected ----

    [Fact]
    public void Detects_Primary_Signature_In_StdOut()
    {
        var stdOut = """
            INFO: ANALYSIS SUCCESSFUL
            INFO: More about the report processing at https://sonar.example.com/api/ce/task?id=AYxxx
            INFO: Task total time: 5.234 s
            ERROR: Task failed with status CANCELED
            """;
        var diag = SonarComputeEngineDiagnostic.Inspect(1, stdOut, "");
        Assert.True(diag.ComputeEngineStallDetected);
        Assert.Equal("Task failed with status CANCELED", diag.MatchedSignature);
        Assert.NotNull(diag.ActionableHint);
        Assert.Contains("docker restart", diag.ActionableHint!);
    }

    [Fact]
    public void Detects_Primary_Signature_In_StdErr()
    {
        var diag = SonarComputeEngineDiagnostic.Inspect(1, "", "Task failed with status CANCELED");
        Assert.True(diag.ComputeEngineStallDetected);
        Assert.Equal("Task failed with status CANCELED", diag.MatchedSignature);
    }

    [Fact]
    public void Detects_Background_Task_Variant()
    {
        var diag = SonarComputeEngineDiagnostic.Inspect(
            exitCode: 1,
            stdOut: "ERROR: Background task failed with status CANCELED\n",
            stdErr: "");
        Assert.True(diag.ComputeEngineStallDetected);
        Assert.Equal("Background task failed with status CANCELED", diag.MatchedSignature);
    }

    [Fact]
    public void Detects_ANALYSIS_REPORT_CANCELED_Wildcard_Pattern()
    {
        // Some SQ versions emit "ANALYSIS_REPORT 'AYxxx' CANCELED" with an ID between.
        var diag = SonarComputeEngineDiagnostic.Inspect(
            exitCode: 1,
            stdOut: "Task ANALYSIS_REPORT 'AY3xkPq8' transitioned to CANCELED",
            stdErr: "");
        Assert.True(diag.ComputeEngineStallDetected);
        Assert.NotNull(diag.MatchedSignature);
    }

    [Fact]
    public void Match_Is_Case_Insensitive()
    {
        var diag = SonarComputeEngineDiagnostic.Inspect(
            exitCode: 1,
            stdOut: "TASK FAILED WITH STATUS CANCELED",
            stdErr: "");
        Assert.True(diag.ComputeEngineStallDetected);
    }

    [Fact]
    public void Hint_Contains_Restart_Command_And_Bug_Reference()
    {
        var diag = SonarComputeEngineDiagnostic.Inspect(1, "Task failed with status CANCELED", "");
        Assert.Contains("docker restart", diag.ActionableHint!);
        Assert.Contains("CeWorkerImpl", diag.ActionableHint!);
        Assert.Contains("Execution time must be positive", diag.ActionableHint!);
    }

    [Fact]
    public void First_Matching_Signature_Wins()
    {
        // When both signatures appear, the more specific one (first in the list) is reported.
        var output = """
            Task failed with status CANCELED
            ANALYSIS_REPORT 'AY1' CANCELED
            """;
        var diag = SonarComputeEngineDiagnostic.Inspect(1, output, "");
        Assert.Equal("Task failed with status CANCELED", diag.MatchedSignature);
    }

    [Fact]
    public void StdErr_Is_Searched_Even_When_StdOut_Is_Null()
    {
        var diag = SonarComputeEngineDiagnostic.Inspect(1, null!, "Task failed with status CANCELED");
        Assert.True(diag.ComputeEngineStallDetected);
    }

    [Fact]
    public void StdOut_Is_Searched_Even_When_StdErr_Is_Null()
    {
        var diag = SonarComputeEngineDiagnostic.Inspect(1, "Task failed with status CANCELED", null!);
        Assert.True(diag.ComputeEngineStallDetected);
    }

    // ---- CaptureResult overload ----

    [Fact]
    public void CaptureResult_Overload_Rejects_Null()
    {
        Assert.Throws<ArgumentNullException>(() => SonarComputeEngineDiagnostic.Inspect(null!));
    }

    [Fact]
    public void CaptureResult_Overload_Delegates_To_StringOverload()
    {
        var capture = new CaptureResult(1, new OutputLine[]
        {
            new(OutputType.Stdout, "ERROR: Task failed with status CANCELED"),
        });
        var diag = SonarComputeEngineDiagnostic.Inspect(capture);
        Assert.True(diag.ComputeEngineStallDetected);
        Assert.Equal("Task failed with status CANCELED", diag.MatchedSignature);
    }

    [Fact]
    public void CaptureResult_Overload_Zero_Exit_Returns_NoStall()
    {
        var capture = new CaptureResult(0, new OutputLine[]
        {
            new(OutputType.Stdout, "INFO: ANALYSIS SUCCESSFUL"),
        });
        var diag = SonarComputeEngineDiagnostic.Inspect(capture);
        Assert.False(diag.ComputeEngineStallDetected);
    }

    [Fact]
    public void CaptureResult_Overload_Searches_Stderr_Lines()
    {
        var capture = new CaptureResult(1, new OutputLine[]
        {
            new(OutputType.Stdout, "INFO: Begin task"),
            new(OutputType.Stderr, "Task failed with status CANCELED"),
        });
        var diag = SonarComputeEngineDiagnostic.Inspect(capture);
        Assert.True(diag.ComputeEngineStallDetected);
    }

    // ---- NoStallDetected singleton invariant ----

    [Fact]
    public void NoStallDetected_Singleton_Has_Expected_Shape()
    {
        Assert.False(SonarPublishDiagnostic.NoStallDetected.ComputeEngineStallDetected);
        Assert.Null(SonarPublishDiagnostic.NoStallDetected.MatchedSignature);
        Assert.Null(SonarPublishDiagnostic.NoStallDetected.ActionableHint);
    }
}
