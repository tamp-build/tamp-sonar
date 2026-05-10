# Tamp.Sonar

SonarScanner wrappers for [Tamp](https://github.com/tamp-build/tamp).

Two packages, **different scanners for different toolchains:**

| Package | Scanner | Use it for |
|---|---|---|
| [`Tamp.SonarScanner.V10`](src/Tamp.SonarScanner.V10) | `dotnet-sonarscanner` (.NET tool) | .NET / MSBuild projects — two-phase begin/end |
| [`Tamp.SonarScannerCli.V6`](src/Tamp.SonarScannerCli.V6) | `sonar-scanner` (Java CLI) | Everything else (TypeScript, Java, Go, Python…) |

Pick one; you almost never use both in the same script.

## Why a separate repo

SonarSource ships scanner releases on its own schedule — the .NET
scanner had a major bump from 9 → 10 in 2024, the Java CLI from 5 → 6
shortly after. Coupling Tamp core's release cadence to those bumps
would either pin Tamp behind Sonar or force a Tamp release every time
SonarSource ships. Splitting `tamp-sonar` out per the Tamp
satellite-repo convention keeps each side moving independently.

## Quick example — `Tamp.SonarScanner.V10` (.NET tool)

```csharp
[NuGetPackage("dotnet-sonarscanner", Version = "10.0.0")]
readonly Tool SonarTool = null!;

[Secret("Sonar token", EnvironmentVariable = "SONAR_TOKEN")]
readonly Secret SonarToken = null!;

Target Compile => _ => _.Executes(() => DotNet.Build(s => s.SetConfiguration(Configuration.Release)));

Target SonarBegin => _ => _
    .Before(nameof(Compile))
    .Executes(() => SonarScanner.Begin(SonarTool, b => b
        .SetProjectKey("acme:my-app")
        .SetHostUrl("https://sonarcloud.io")
        .SetOrganization("acme")
        .SetToken(SonarToken)));

Target SonarEnd => _ => _
    .After(nameof(Compile))
    .Executes(() => SonarScanner.End(SonarTool, e => e.SetToken(SonarToken)));
```

## Quick example — `Tamp.SonarScannerCli.V6` (Java CLI)

```csharp
[NuGetPackage("sonar-scanner", UseSystemPath = true, ExecutableName = "sonar-scanner")]
readonly Tool SonarCli = null!;

Target Sonar => _ => _.Executes(() => SonarScannerCli.Scan(SonarCli, s => s
    .SetProjectKey("acme:frontend")
    .SetHostUrl("https://sonarcloud.io")
    .SetToken(SonarToken)
    .SetSources("src,lib")));
```

## License

[MIT](LICENSE) — same as `tamp` core.
