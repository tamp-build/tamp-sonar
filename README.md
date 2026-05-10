# Tamp.Sonar

SonarScanner wrappers for [Tamp](https://github.com/tamp-build/tamp).

Two packages, **different scanners for different toolchains** — pick one;
you almost never use both in the same script.

| Package | Scanner | Use it for | Status |
|---|---|---|---|
| [`Tamp.SonarScanner.V10`](src/Tamp.SonarScanner.V10) | `dotnet-sonarscanner` (.NET tool) | .NET / MSBuild — two-phase begin/end | preview |
| [`Tamp.SonarScannerCli.V6`](src/Tamp.SonarScannerCli.V6) | `sonar-scanner` (Java CLI) | Everything else (TS, Java, Go, Python…) | preview |

Tokens are typed as `Secret` and registered with the runner's redaction
table.

Requires `Tamp.Core ≥ 1.0.0`.

## Why a separate repo

SonarSource ships scanner releases on its own schedule — the .NET scanner
went 9 → 10 in 2024, the Java CLI 5 → 6 shortly after. Coupling `tamp`
core's release cadence to those bumps would either pin `tamp` behind
Sonar or force a `tamp` release every time SonarSource ships. Per the
satellite-repo convention, this repo tracks SonarSource's cadence
independently.

## Install

In your build script's `Directory.Packages.props`:

```xml
<PackageVersion Include="Tamp.SonarScanner.V10"   Version="0.0.1-alpha" />
<!-- or -->
<PackageVersion Include="Tamp.SonarScannerCli.V6" Version="0.0.1-alpha" />
```

In `build/Build.csproj`:

```xml
<PackageReference Include="Tamp.SonarScanner.V10" />
```

## Quick example — `Tamp.SonarScanner.V10` (.NET tool, two-phase)

```csharp
using Tamp;
using Tamp.NetCli.V10;
using Tamp.SonarScanner.V10;

[NuGetPackage("dotnet-sonarscanner", Version = "10.0.0")]
readonly Tool SonarTool = null!;

// Until TAM-78 lands [Secret] env-var resolution in Tamp.Core 1.0.1.
static readonly Secret? SonarToken =
    Environment.GetEnvironmentVariable("SONAR_TOKEN") is { Length: > 0 } v
        ? new Secret("Sonar token", v) : null;

Target Compile => _ => _.Executes(() => DotNet.Build(s => s.SetConfiguration(Configuration.Release)));

Target SonarBegin => _ => _
    .Before(nameof(Compile))
    .Requires(() => SonarToken != null)
    .Executes(() => SonarScanner.Begin(SonarTool, b => b
        .SetProjectKey("acme:my-app")
        .SetHostUrl("https://sonarcloud.io")
        .SetOrganization("acme")
        .SetToken(SonarToken!)));

Target SonarEnd => _ => _
    .After(nameof(Compile))
    .Executes(() => SonarScanner.End(SonarTool, e => e.SetToken(SonarToken!)));
```

## Quick example — `Tamp.SonarScannerCli.V6` (Java CLI, single-phase)

```csharp
using Tamp;
using Tamp.SonarScannerCli.V6;

[NuGetPackage("sonar-scanner", UseSystemPath = true, ExecutableName = "sonar-scanner")]
readonly Tool SonarCli = null!;

Target Sonar => _ => _
    .Requires(() => SonarToken != null)
    .Executes(() => SonarScannerCli.Scan(SonarCli, s => s
        .SetProjectKey("acme:frontend")
        .SetHostUrl("https://sonarcloud.io")
        .SetToken(SonarToken!)
        .SetSources("src,lib")));
```

## See also

- [tamp](https://github.com/tamp-build/tamp) — the core framework
- [Tamp ADR 0002](https://github.com/tamp-build/tamp/blob/main/docs/adr/0002-package-naming-convention.md) — package naming convention

## License

[MIT](LICENSE) — same as `tamp` core. (Sonar tools themselves are LGPL/various.)
