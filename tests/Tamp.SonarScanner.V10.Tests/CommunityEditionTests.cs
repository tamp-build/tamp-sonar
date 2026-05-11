using System.IO;
using System.Text.Json;
using System.Xml;
using Xunit;

namespace Tamp.SonarScanner.V10.Tests;

/// <summary>
/// Covers <see cref="SonarBeginSettings.CommunityEdition"/> and
/// <see cref="SonarScanner.StripBranchPropertiesFromAnalysisConfigXml"/>
/// — the post-TAM-98 surface for SonarQube CE-friendly pipelines.
/// </summary>
public sealed class CommunityEditionTests : IDisposable
{
    private readonly string _origScannerParams;
    private static readonly object _envLock = new();

    public CommunityEditionTests()
    {
        // Capture and clear so per-test setup doesn't leak between tests
        // (xunit runs tests in parallel by default within a class — we
        // serialize CE tests via the lock + capture/restore pattern).
        _origScannerParams = Environment.GetEnvironmentVariable("SONARQUBE_SCANNER_PARAMS") ?? string.Empty;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SONARQUBE_SCANNER_PARAMS",
            string.IsNullOrEmpty(_origScannerParams) ? null : _origScannerParams);
    }

    private static Tool FakeTool() => new(AbsolutePath.Create("/fake/dotnet-sonarscanner"));

    // ---------- env-var cleanup ----------

    [Fact]
    public void CommunityEdition_Strips_Branch_Properties_From_Inherited_Env()
    {
        lock (_envLock)
        {
            // Simulate the ADO SonarSource extension's injection.
            var injected = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["sonar.host.url"] = "https://sonar.example.com",
                ["sonar.branch.name"] = "feature/auth",
                ["sonar.branch.target"] = "main",
                ["sonar.projectKey"] = "strata-api",
            });
            Environment.SetEnvironmentVariable("SONARQUBE_SCANNER_PARAMS", injected);

            var plan = SonarScanner.Begin(FakeTool(), s => s
                .SetProjectKey("strata-api")
                .SetCommunityEdition());

            Assert.True(plan.Environment.TryGetValue("SONARQUBE_SCANNER_PARAMS", out var cleaned));
            using var doc = JsonDocument.Parse(cleaned!);
            var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
            Assert.Contains("sonar.host.url", keys);
            Assert.Contains("sonar.projectKey", keys);
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

            var plan = SonarScanner.Begin(FakeTool(), s => s
                .SetProjectKey("strata-api")
                .SetCommunityEdition());

            Assert.Equal("{}", plan.Environment["SONARQUBE_SCANNER_PARAMS"]);
        }
    }

    [Fact]
    public void CommunityEdition_Does_Not_Set_EnvVar_When_Flag_Off()
    {
        lock (_envLock)
        {
            Environment.SetEnvironmentVariable("SONARQUBE_SCANNER_PARAMS", "{\"sonar.branch.name\":\"x\"}");

            // Flag NOT set — wrapper leaves env alone.
            var plan = SonarScanner.Begin(FakeTool(), s => s.SetProjectKey("strata-api"));

            Assert.False(plan.Environment.ContainsKey("SONARQUBE_SCANNER_PARAMS"),
                "Wrapper shouldn't mutate inherited env when CommunityEdition flag is off.");
        }
    }

    [Fact]
    public void CommunityEdition_Drops_Forbidden_Properties_From_Argv()
    {
        lock (_envLock)
        {
            Environment.SetEnvironmentVariable("SONARQUBE_SCANNER_PARAMS", null);

            // Caller set CE AND accidentally passed a forbidden property
            // via SetProperty. The wrapper drops it silently — belt-and-braces.
            var plan = SonarScanner.Begin(FakeTool(), s => s
                .SetProjectKey("strata-api")
                .SetCommunityEdition()
                .SetProperty("sonar.branch.name", "feature/x")
                .SetProperty("sonar.exclusions", "**/bin/**"));

            Assert.DoesNotContain("/d:sonar.branch.name=feature/x", plan.Arguments);
            Assert.Contains("/d:sonar.exclusions=**/bin/**", plan.Arguments);
        }
    }

    [Fact]
    public void CommunityEdition_With_Explicit_SONAR_PARAMS_Env_Strips_Forbidden_Keys()
    {
        lock (_envLock)
        {
            Environment.SetEnvironmentVariable("SONARQUBE_SCANNER_PARAMS", null);

            // Caller set a custom value via SetEnv (overriding inherited),
            // BUT the custom value still has the forbidden keys. We
            // still strip them.
            var custom = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["sonar.host.url"] = "https://custom.example.com",
                ["sonar.branch.name"] = "should-be-removed",
            });

            var plan = SonarScanner.Begin(FakeTool(), s =>
            {
                s.SetProjectKey("strata-api").SetCommunityEdition();
                s.EnvironmentVariables["SONARQUBE_SCANNER_PARAMS"] = custom;
            });

            using var doc = JsonDocument.Parse(plan.Environment["SONARQUBE_SCANNER_PARAMS"]);
            var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
            Assert.Contains("sonar.host.url", keys);
            Assert.DoesNotContain("sonar.branch.name", keys);
        }
    }

    [Fact]
    public void CommunityEdition_With_NonJson_Inherited_Env_Leaves_It_Alone()
    {
        lock (_envLock)
        {
            // Garbage in SONARQUBE_SCANNER_PARAMS — the scanner will reject
            // it on its own. We don't try to fix it.
            Environment.SetEnvironmentVariable("SONARQUBE_SCANNER_PARAMS", "not-json-at-all");

            var plan = SonarScanner.Begin(FakeTool(), s => s
                .SetProjectKey("strata-api")
                .SetCommunityEdition());

            // Either absent or unchanged. The wrapper shouldn't have
            // emitted a cleaned version.
            if (plan.Environment.TryGetValue("SONARQUBE_SCANNER_PARAMS", out var val))
                Assert.Equal("not-json-at-all", val);
        }
    }

    [Fact]
    public void DisableBranchProperties_Alias_Behaves_The_Same()
    {
        lock (_envLock)
        {
            Environment.SetEnvironmentVariable("SONARQUBE_SCANNER_PARAMS",
                "{\"sonar.branch.name\":\"x\",\"sonar.host.url\":\"https://x\"}");

            var plan = SonarScanner.Begin(FakeTool(), s => s
                .SetProjectKey("strata-api")
                .DisableBranchProperties());

            using var doc = JsonDocument.Parse(plan.Environment["SONARQUBE_SCANNER_PARAMS"]);
            var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
            Assert.Contains("sonar.host.url", keys);
            Assert.DoesNotContain("sonar.branch.name", keys);
        }
    }

    // ---------- analysis xml strip ----------

    [Fact]
    public void StripBranchProperties_Removes_Property_Elements_By_Name_Attribute()
    {
        var workdir = NewTempDir();
        try
        {
            var xmlPath = Path.Combine(workdir, "SonarQubeAnalysisConfig.xml");
            File.WriteAllText(xmlPath, """
                <?xml version="1.0" encoding="utf-8"?>
                <SonarQubeAnalysisConfig xmlns="http://www.sonarsource.com/msbuild/integration/2015/1">
                  <AdditionalConfig>
                    <ConfigSetting Id="sonar.host.url" Value="https://sonar.example.com" />
                    <Property Name="sonar.branch.name">feature/auth</Property>
                    <Property Name="sonar.branch.target">main</Property>
                    <Property Name="sonar.exclusions">**/bin/**</Property>
                  </AdditionalConfig>
                </SonarQubeAnalysisConfig>
                """);

            var removed = SonarScanner.StripBranchPropertiesFromAnalysisConfigXml(xmlPath);
            Assert.Equal(2, removed);

            var after = File.ReadAllText(xmlPath);
            Assert.DoesNotContain("sonar.branch.name", after);
            Assert.DoesNotContain("sonar.branch.target", after);
            Assert.Contains("sonar.host.url", after);
            Assert.Contains("sonar.exclusions", after);
        }
        finally { Directory.Delete(workdir, recursive: true); }
    }

    [Fact]
    public void StripBranchProperties_Removes_Setting_Elements_By_Id_Attribute()
    {
        // Less-common shape: <Setting Id="sonar.branch.name">…
        var workdir = NewTempDir();
        try
        {
            var xmlPath = Path.Combine(workdir, "SonarQubeAnalysisConfig.xml");
            File.WriteAllText(xmlPath, """
                <?xml version="1.0" encoding="utf-8"?>
                <root>
                  <Setting Id="sonar.branch.name"><Value>main</Value></Setting>
                  <Setting Id="sonar.host.url"><Value>https://x</Value></Setting>
                </root>
                """);

            var removed = SonarScanner.StripBranchPropertiesFromAnalysisConfigXml(xmlPath);
            Assert.Equal(1, removed);
            var after = File.ReadAllText(xmlPath);
            Assert.DoesNotContain("sonar.branch.name", after);
            Assert.Contains("sonar.host.url", after);
        }
        finally { Directory.Delete(workdir, recursive: true); }
    }

    [Fact]
    public void StripBranchProperties_Walks_Directory_To_Find_Every_Config_File()
    {
        // The scanner writes per-MSBuild-project subdirs under .sonarqube/.
        // The helper walks recursively so all of them get cleaned.
        var sonarqubeDir = NewTempDir();
        try
        {
            var confDir = Path.Combine(sonarqubeDir, "conf");
            var projDir = Path.Combine(sonarqubeDir, "out", "0");
            Directory.CreateDirectory(confDir);
            Directory.CreateDirectory(projDir);

            var content = """
                <?xml version="1.0"?>
                <root><Property Name="sonar.branch.name">x</Property></root>
                """;
            File.WriteAllText(Path.Combine(confDir, "SonarQubeAnalysisConfig.xml"), content);
            File.WriteAllText(Path.Combine(projDir, "SonarQubeAnalysisConfig.xml"), content);

            var removed = SonarScanner.StripBranchPropertiesFromAnalysisConfigXml(sonarqubeDir);
            Assert.Equal(2, removed); // one per file
        }
        finally { Directory.Delete(sonarqubeDir, recursive: true); }
    }

    [Fact]
    public void StripBranchProperties_File_Without_Forbidden_Keys_Returns_Zero_And_Unmodified()
    {
        var workdir = NewTempDir();
        try
        {
            var xmlPath = Path.Combine(workdir, "SonarQubeAnalysisConfig.xml");
            var content = """
                <?xml version="1.0"?>
                <root><Property Name="sonar.host.url">https://x</Property></root>
                """;
            File.WriteAllText(xmlPath, content);
            var before = File.ReadAllText(xmlPath);

            var removed = SonarScanner.StripBranchPropertiesFromAnalysisConfigXml(xmlPath);
            Assert.Equal(0, removed);
            Assert.Equal(before, File.ReadAllText(xmlPath));
        }
        finally { Directory.Delete(workdir, recursive: true); }
    }

    [Fact]
    public void StripBranchProperties_Nonexistent_Path_Is_NoOp()
    {
        // Tests / cleanup tasks might run before Begin has written anything.
        var removed = SonarScanner.StripBranchPropertiesFromAnalysisConfigXml(
            "/definitely/not/a/real/path/" + Guid.NewGuid());
        Assert.Equal(0, removed);
    }

    [Fact]
    public void StripBranchProperties_Null_Or_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => SonarScanner.StripBranchPropertiesFromAnalysisConfigXml(""));
        Assert.Throws<ArgumentException>(() => SonarScanner.StripBranchPropertiesFromAnalysisConfigXml(null!));
    }

    private static string NewTempDir()
    {
        var p = Path.Combine(Path.GetTempPath(), $"tamp-sonar-ce-{Guid.NewGuid():N}");
        Directory.CreateDirectory(p);
        return p;
    }
}
