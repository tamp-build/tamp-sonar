using System.Text.Json;
using Xunit;

namespace Tamp.SonarScannerCli.V6.Tests;

/// <summary>
/// Covers <see cref="SonarScanSettings.CommunityEdition"/> for the
/// Java CLI scanner. No XML cleanup test — the Java scanner doesn't
/// emit a SonarQubeAnalysisConfig.xml file.
/// </summary>
public sealed class CommunityEditionTests : IDisposable
{
    private readonly string _origScannerParams;
    private static readonly object _envLock = new();

    public CommunityEditionTests()
    {
        _origScannerParams = Environment.GetEnvironmentVariable("SONARQUBE_SCANNER_PARAMS") ?? string.Empty;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SONARQUBE_SCANNER_PARAMS",
            string.IsNullOrEmpty(_origScannerParams) ? null : _origScannerParams);
    }

    private static Tool FakeTool() => new(AbsolutePath.Create("/fake/sonar-scanner"));

    [Fact]
    public void CommunityEdition_Strips_Branch_Properties_From_Inherited_Env()
    {
        lock (_envLock)
        {
            var injected = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["sonar.host.url"] = "https://sonar.example.com",
                ["sonar.branch.name"] = "feature/auth",
                ["sonar.branch.target"] = "main",
            });
            Environment.SetEnvironmentVariable("SONARQUBE_SCANNER_PARAMS", injected);

            var plan = SonarScannerCli.Scan(FakeTool(), s => s.SetCommunityEdition());

            Assert.True(plan.Environment.TryGetValue("SONARQUBE_SCANNER_PARAMS", out var cleaned));
            using var doc = JsonDocument.Parse(cleaned!);
            var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
            Assert.Contains("sonar.host.url", keys);
            Assert.DoesNotContain("sonar.branch.name", keys);
            Assert.DoesNotContain("sonar.branch.target", keys);
        }
    }

    [Fact]
    public void CommunityEdition_Without_Inherited_Env_Emits_Empty_Object_Defensively()
    {
        lock (_envLock)
        {
            Environment.SetEnvironmentVariable("SONARQUBE_SCANNER_PARAMS", null);
            var plan = SonarScannerCli.Scan(FakeTool(), s => s.SetCommunityEdition());
            Assert.Equal("{}", plan.Environment["SONARQUBE_SCANNER_PARAMS"]);
        }
    }

    [Fact]
    public void CommunityEdition_Off_Leaves_Inherited_Env_Alone()
    {
        lock (_envLock)
        {
            Environment.SetEnvironmentVariable("SONARQUBE_SCANNER_PARAMS", "{\"sonar.branch.name\":\"x\"}");
            var plan = SonarScannerCli.Scan(FakeTool(), s => s.SetProjectKey("k"));
            Assert.False(plan.Environment.ContainsKey("SONARQUBE_SCANNER_PARAMS"));
        }
    }

    [Fact]
    public void CommunityEdition_Drops_Forbidden_D_Property_From_Argv()
    {
        lock (_envLock)
        {
            Environment.SetEnvironmentVariable("SONARQUBE_SCANNER_PARAMS", null);

            var plan = SonarScannerCli.Scan(FakeTool(), s => s
                .SetProjectKey("strata-api")
                .SetCommunityEdition()
                .SetProperty("sonar.branch.name", "feature/x")
                .SetProperty("sonar.exclusions", "**/bin/**"));

            Assert.DoesNotContain("-Dsonar.branch.name=feature/x", plan.Arguments);
            Assert.Contains("-Dsonar.exclusions=**/bin/**", plan.Arguments);
        }
    }

    [Fact]
    public void DisableBranchProperties_Alias_Behaves_The_Same()
    {
        lock (_envLock)
        {
            Environment.SetEnvironmentVariable("SONARQUBE_SCANNER_PARAMS",
                "{\"sonar.branch.name\":\"x\",\"sonar.host.url\":\"https://x\"}");

            var plan = SonarScannerCli.Scan(FakeTool(), s => s.DisableBranchProperties());

            using var doc = JsonDocument.Parse(plan.Environment["SONARQUBE_SCANNER_PARAMS"]);
            var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
            Assert.Contains("sonar.host.url", keys);
            Assert.DoesNotContain("sonar.branch.name", keys);
        }
    }
}
