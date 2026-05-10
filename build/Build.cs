using Tamp;
using Tamp.NetCli.V10;

/// <summary>
/// tamp-sonar's self-hosted build script. Packs both
/// Tamp.SonarScanner.V10 (.NET tool) and Tamp.SonarScannerCli.V6
/// (Java CLI) — two separate scanners from the same SonarSource family.
/// </summary>
class Build : TampBuild
{
    public static int Main(string[] args) => Execute<Build>(args);

    [Parameter("Build configuration")]
    Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Parameter("Package version override", EnvironmentVariable = "PACKAGE_VERSION")]
#pragma warning disable CS0649
    readonly string? Version;
#pragma warning restore CS0649

    [Solution] readonly Solution Solution = null!;
    [GitRepository] readonly GitRepository Git = null!;

    static readonly Secret? NuGetApiKey =
        Environment.GetEnvironmentVariable("NUGET_API_KEY") is { Length: > 0 } v
            ? new Secret("NuGet API key", v)
            : null;

    AbsolutePath Artifacts => RootDirectory / "artifacts";

    Target Info => _ => _.Executes(() =>
    {
        Console.WriteLine($"  Branch:        {Git.Branch ?? "<detached>"}");
        Console.WriteLine($"  Commit:        {Git.Commit[..7]}");
        Console.WriteLine($"  Configuration: {Configuration}");
        Console.WriteLine($"  Solution:      {Solution.Name} ({Solution.Projects.Count} projects)");
    });

    Target Clean => _ => _
        .TopLevel()
        .Executes(() =>
        {
            foreach (var d in RootDirectory.GlobDirectories("**/bin", "**/obj")) d.Delete();
            Artifacts.Delete();
        });

    Target Restore => _ => _
        .Executes(() => DotNet.Restore(s => s.SetProject(Solution.Path)));

    Target Compile => _ => _
        .TopLevel()
        .DependsOn(nameof(Restore))
        .Executes(() => DotNet.Build(s => s
            .SetProject(Solution.Path)
            .SetConfiguration(Configuration)
            .SetNoRestore(true)));

    Target Test => _ => _
        .TopLevel()
        .DependsOn(nameof(Compile))
        .Executes(() => new[]
        {
            DotNet.Test(s => s
                .SetProject(RootDirectory / "tests" / "Tamp.SonarScanner.V10.Tests" / "Tamp.SonarScanner.V10.Tests.csproj")
                .SetConfiguration(Configuration)
                .SetNoBuild(true)
                .AddLogger("trx;LogFileName=sonarscanner-v10.trx")
                .SetResultsDirectory(Artifacts / "test-results")),
            DotNet.Test(s => s
                .SetProject(RootDirectory / "tests" / "Tamp.SonarScannerCli.V6.Tests" / "Tamp.SonarScannerCli.V6.Tests.csproj")
                .SetConfiguration(Configuration)
                .SetNoBuild(true)
                .AddLogger("trx;LogFileName=sonarscannercli-v6.trx")
                .SetResultsDirectory(Artifacts / "test-results")),
        });

    Target Pack => _ => _
        .TopLevel()
        .DependsOn(nameof(Test))
        .Description("Pack both Sonar wrapper packages.")
        .Executes(() => new[]
        {
            DotNet.Pack(s =>
            {
                s.SetProject(RootDirectory / "src" / "Tamp.SonarScanner.V10" / "Tamp.SonarScanner.V10.csproj");
                s.SetConfiguration(Configuration);
                s.SetNoBuild(true);
                s.SetOutput(Artifacts);
                if (!string.IsNullOrEmpty(Version)) s.SetProperty("Version", Version);
            }),
            DotNet.Pack(s =>
            {
                s.SetProject(RootDirectory / "src" / "Tamp.SonarScannerCli.V6" / "Tamp.SonarScannerCli.V6.csproj");
                s.SetConfiguration(Configuration);
                s.SetNoBuild(true);
                s.SetOutput(Artifacts);
                if (!string.IsNullOrEmpty(Version)) s.SetProperty("Version", Version);
            }),
        });

    Target Push => _ => _
        .TopLevel()
        .DependsOn(nameof(Pack))
        .Requires(() => NuGetApiKey != null)
        .Executes(() => Artifacts.GlobFiles("*.nupkg")
            .Select(p => DotNet.NuGetPush(s => s
                .SetPackagePath(p)
                .SetSource("https://api.nuget.org/v3/index.json")
                .SetApiKey(NuGetApiKey!)
                .SetSkipDuplicate(true))));

    Target Ci => _ => _
        .TopLevel()
        .DependsOn(nameof(Info), nameof(Clean), nameof(Pack))
        .Description("Full CI pipeline.");

    Target Default => _ => _.DependsOn(nameof(Compile));
}
