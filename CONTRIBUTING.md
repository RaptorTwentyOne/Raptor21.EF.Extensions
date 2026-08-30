# Contributing

## Building

```bash
dotnet build Raptor21.EF.Extensions.slnx -c Release
dotnet test  Raptor21.EF.Extensions.slnx -c Release --no-build
dotnet pack  Raptor21.EF.Extensions.slnx -c Release --no-build -o artifacts
```

The runtime library multi-targets `net8.0` and `net10.0`, so both SDKs need to be installed. The
generator targets `netstandard2.0` because Roslyn loads analyzers there; that is not negotiable.

## The two halves, and why they stay apart

The runtime library resolves nothing by reflection. Every procedure call goes through a contract that
the source generator wrote at compile time, and every result row is materialised by a generated
`FromDataRecord`. That is the only reason a consumer can publish AOT, and it is why the generator is a
separate project rather than a runtime feature: the moment schema discovery moves to runtime, trimming
and AOT stop working.

The generator is a separate *project* but not a separate *package*. It is packed into
`Raptor21.EF.Extensions.StoredProcedures` under `analyzers/dotnet/cs`, so a consumer cannot install one
half without the other. CI opens the produced package and fails if the analyzer file is not in it.

## Running the sample against a real database

The sample needs SQL Server. Point it at one and run it:

```bash
export SAMPLE_SQL_CONNECTION="Server=localhost;Database=Raptor21EfExtensionsSample;User Id=sa;Password=...;TrustServerCertificate=True"
dotnet run --project samples/Raptor21.EF.Extensions.Sample
```

Without the variable it falls back to LocalDB. Never commit a connection string; `.gitignore` covers the
usual local settings files, but the environment variable is the intended route.

## Releasing

Versions come from tags, not from the checked-in `VersionPrefix`. Push a tag matching `v*.*.*` and
`release.yml` builds, tests, packs, publishes to nuget.org through trusted publishing (OIDC — there is
no API key in this repository) and attaches the packages to a GitHub release.

Before the first release the repository needs: the `production` GitHub environment, the repository
variable `NUGET_USER`, and one nuget.org trusted-publisher policy per package id. A tag pushed before
those exist fails at the publish step.

## Conventions

- MIT, `Authors=RaptorTwentyOne`, package metadata lives in `Directory.Build.props`.
- 4-space indent, 2 for project and config files; LF in the repository (see `.editorconfig`,
  `.gitattributes`).
- XML docs are on with CS1591 suppressed. Suppressed is not the same as unnecessary — public members
  still owe their comments.
