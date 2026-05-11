using Xunit;

namespace Tamp.SonarScanner.V10.Tests;

// ---- Object-init overloads (TAM-161, 0.3.1+) ----
public sealed class ObjectInitTests
{
    private static Tool FakeTool() => new(AbsolutePath.Create("/fake/dotnet-sonarscanner"));

    [Fact]
    public void Begin_ObjectInit_Emits_Identical_Plan_To_Fluent()
    {
        var tool = FakeTool();
        var token = new Secret("SonarToken", "abc123");

        var fluent = SonarScanner.Begin(tool, s => s
            .SetProjectKey("acme:lib")
            .SetProjectName("Acme Library")
            .SetProjectVersion("1.2.3")
            .SetHostUrl("https://sonarqube.example.com")
            .SetOrganization("acme-org")
            .SetToken(token)
            .SetVerbose(true)
            .SetProperty("sonar.exclusions", "**/*.generated.cs"));

        var objectInit = SonarScanner.Begin(tool, new SonarBeginSettings
        {
            ProjectKey = "acme:lib",
            ProjectName = "Acme Library",
            ProjectVersion = "1.2.3",
            HostUrl = "https://sonarqube.example.com",
            Organization = "acme-org",
            Token = token,
            Verbose = true,
            AdditionalProperties = { ["sonar.exclusions"] = "**/*.generated.cs" },
        });

        Assert.Equal(fluent.Executable, objectInit.Executable);
        Assert.Equal(fluent.Arguments, objectInit.Arguments);
        Assert.Equal(fluent.WorkingDirectory, objectInit.WorkingDirectory);
        Assert.Equal(fluent.Secrets.Count, objectInit.Secrets.Count);
    }

    [Fact]
    public void End_ObjectInit_Emits_Identical_Plan_To_Fluent()
    {
        var tool = FakeTool();
        var token = new Secret("SonarToken", "abc123");

        var fluent = SonarScanner.End(tool, s => s
            .SetToken(token)
            .SetProperty("sonar.qualitygate.wait", "true"));

        var objectInit = SonarScanner.End(tool, new SonarEndSettings
        {
            Token = token,
            AdditionalProperties = { ["sonar.qualitygate.wait"] = "true" },
        });

        Assert.Equal(fluent.Arguments, objectInit.Arguments);
        Assert.Equal(fluent.Secrets.Count, objectInit.Secrets.Count);
    }

    [Fact]
    public void Begin_ObjectInit_Requires_ProjectKey()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SonarScanner.Begin(FakeTool(), new SonarBeginSettings()));
    }

    [Fact]
    public void All_ObjectInit_Overloads_Return_NonNull_CommandPlan()
    {
        // Smoke test: each wrapper accepts an object-init settings argument and returns a non-null CommandPlan.
        var tool = FakeTool();
        Assert.NotNull(SonarScanner.Begin(tool, new SonarBeginSettings { ProjectKey = "k" }));
        Assert.NotNull(SonarScanner.End(tool, new SonarEndSettings()));
    }
}
