using Tamp;
using Tamp.NetCli.V10;
using Tamp.Telegram;
using Tamp.Components;
using Tamp.Components.NetCli.V10;

/// <summary>
/// tamp-sonar's self-hosted build script. Packs both
/// Tamp.SonarScanner.V10 (.NET tool) and Tamp.SonarScannerCli.V6
/// (Java CLI) — two separate scanners from the same SonarSource family.
/// </summary>
class Build : TampBuild, IDotNetPack
{
    public static int Main(string[] args) => Execute<Build>(args);

    // TAM-227 — Telegram failure notify. Pulls TELEGRAM_BOT_TOKEN /
    // TELEGRAM_CHAT_ID / TELEGRAM_BUILD_LABEL from the environment;
    // returns null when missing, framework silently skips null reporters.
    [BuildReporter] readonly IBuildReporter? TelegramNotify =
        TelegramBuildReporter.FromEnvironment();

    [Parameter("Build configuration")]
    public Configuration Configuration { get; set; } = IsLocalBuild ? Configuration.Debug : Configuration.Release;


    [Solution] public Solution Solution { get; set; } = null!;
    [GitRepository] readonly GitRepository Git = null!;

    // Bound by SecretBinder from NUGET_API_KEY env var (TAM-78,

    // Tamp.Core 1.0.1). CI masking via TampBuild.RegisterSecretForCiMasking.

    [Secret("NuGet API key", EnvironmentVariable = "NUGET_API_KEY")]

    readonly Secret NuGetApiKey = null!;

    AbsolutePath Artifacts => RootDirectory / "artifacts";

    public AbsolutePath ArtifactsDirectory => Artifacts;

    Target Info => _ => _.Executes(() =>
    {
        Console.WriteLine($"  Branch:        {Git.Branch ?? "<detached>"}");
        Console.WriteLine($"  Commit:        {Git.Commit[..7]}");
        Console.WriteLine($"  Configuration: {Configuration}");
    });

    Target Clean => _ => _
        .Description("Delete bin/obj and the artifacts directory.")
        .Executes(() => CleanArtifacts());

    Target Test => _ => _
        .DependsOn(nameof(ICompile.Compile))
        .Executes(() => new[]
        {
            DotNet.Test(s => s
                .SetProject(RootDirectory / "tests" / "Tamp.SonarScanner.V10.Tests" / "Tamp.SonarScanner.V10.Tests.csproj")
                .SetConfiguration(Configuration)
                .SetNoBuild(true)
                .AddLogger("trx;LogFileName=sonarscanner-v10.trx")
                .AddDataCollector("XPlat Code Coverage")
                .SetSettings((RootDirectory / "build" / "coverlet.runsettings").Value)
                .SetResultsDirectory(Artifacts / "test-results")),
            DotNet.Test(s => s
                .SetProject(RootDirectory / "tests" / "Tamp.SonarScannerCli.V6.Tests" / "Tamp.SonarScannerCli.V6.Tests.csproj")
                .SetConfiguration(Configuration)
                .SetNoBuild(true)
                .AddLogger("trx;LogFileName=sonarscannercli-v6.trx")
                .AddDataCollector("XPlat Code Coverage")
                .SetSettings((RootDirectory / "build" / "coverlet.runsettings").Value)
                .SetResultsDirectory(Artifacts / "test-results")),
        });

    Target Push => _ => _
        .DependsOn(nameof(IPack.Pack))
        .Requires(() => NuGetApiKey != null)
        .Executes(() => Artifacts.GlobFiles("*.nupkg")
            .Select(p => DotNet.NuGetPush(s => s
                .SetPackagePath(p)
                .SetSource("https://api.nuget.org/v3/index.json")
                .SetApiKey(NuGetApiKey)
                .SetSkipDuplicate(true))));

    Target Ci => _ => _
        .DependsOn(nameof(Info), nameof(Clean), nameof(Test), nameof(IPack.Pack))
        .Description("Full CI pipeline.");

    Target Default => _ => _.DependsOn(nameof(ICompile.Compile));

    // ----- Sonar (TAM-17) -----

    [NuGetPackage("dotnet-sonarscanner", Version = "10.4.1")]
    readonly Tool SonarTool = null!;


    [Secret("SonarQube token", EnvironmentVariable = "SONAR_TOKEN")]


    readonly Secret SonarToken = null!;

    [Parameter("Sonar host URL", EnvironmentVariable = "SONAR_HOST_URL")]
    readonly string SonarHostUrl = "https://sonar.brewingcoder.com";

    [Parameter("Sonar project key")]
    readonly string SonarProjectKey = "tamp-build_tamp-sonar";

    Target SonarBegin => _ => _
        .Description("Initialize the SonarScanner pre-build phase.")
        .Before(nameof(ICompile.Compile))
        .Requires(() => SonarToken != null)
        .Executes(() => Tamp.SonarScanner.V10.SonarScanner.Begin(SonarTool, s =>
        {
            s.SetProjectKey(SonarProjectKey);
            s.SetHostUrl(SonarHostUrl);
            s.SetToken(SonarToken);
            s.SetProperty("sonar.cs.vstest.reportsPaths", $"{(Artifacts / "test-results").Value}/**/*.trx");
            s.SetProperty("sonar.cs.opencover.reportsPaths", $"{(Artifacts / "test-results").Value}/**/coverage.opencover.xml");

            s.SetProperty("sonar.coverage.exclusions", "tests/**,build/**,samples/**");

            s.SetProperty("sonar.exclusions", "**/bin/**,**/obj/**,artifacts/**,build/**,docs/**,samples/**");
        }));

    Target SonarEnd => _ => _
        .Description("Finalize SonarScanner and submit results to the server.")
        .DependsOn(nameof(Test))
        .Requires(() => SonarToken != null)
        .Executes(() => Tamp.SonarScanner.V10.SonarScanner.End(SonarTool, s => s.SetToken(SonarToken)));

    Target Sonar => _ => _
        .DependsOn(nameof(SonarBegin), nameof(SonarEnd))
        .Description("End-to-end Sonar scan: Begin (before Compile) → Compile → Test → End. Requires SONAR_TOKEN.");

}
