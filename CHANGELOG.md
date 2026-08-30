# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the version is 0.x the public
surface is not a compatibility commitment.

## [Unreleased]

### Added

- `Raptor21.EF.Extensions.StoredProcedures` — the runtime: contracts, a contract-driven executor, a
  live-schema validator and an idempotent embedded-script applier. Multi-targets `net8.0` and `net10.0`,
  marked `IsTrimmable` and `IsAotCompatible`; the trim and AOT analyzers report nothing on it.
- The Roslyn source generator, shipped **inside** that package under `analyzers/dotnet/cs`. One
  `PackageReference` gives a consumer both halves, and there is no way to pair a runtime with a
  generator of a different version. CI opens the package and fails if the analyzer is missing.
- `Raptor21.EF.Extensions.Migrations` — an `IMigrationsModelDiffer` that registers procedure scripts as
  model annotations and emits `CREATE OR ALTER` / `DROP PROCEDURE IF EXISTS` operations, so procedure
  changes ride in the same migration as the table changes they depend on. Targets EF Core 10.
- `samples/Raptor21.EF.Extensions.Sample` — a console app that migrates a table and four procedures from
  one `dotnet ef migrations add`, validates every generated contract against the live schema at startup,
  and calls the procedures through the generated API.
- Org packaging: MIT, tag-driven versioning, SourceLink, symbol packages, CI on `master`, and release by
  nuget.org trusted publishing (OIDC).

### Changed

- Extracted from the KnightOnline repository, where this lived as `SqlServerStoredProcedureSchema`.
  Namespaces, assembly names and package ids were renamed to the `Raptor21.EF.Extensions` family; the
  EF integration dropped the redundant `.EntityFrameworkCore` suffix in favour of `.Migrations`.
  Nothing else about the code changed in the move: the same 25 tests pass before and after.
- Dropped the KnightOnline root `Directory.Build.props`, which injected a `BuildInfo.Versions` type into
  every assembly it built — including, previously, the shipped ones.

### Fixed

- `dotnet pack` of the generator used to fail with NU5017 (`IncludeBuildOutput=false` and nothing to
  pack). The generator is no longer its own package, so the failure is gone by construction.

### Known gaps

Carried over from the audit that preceded this extraction; none of them is a regression.

- Only stored procedures are first-class. Functions, views, triggers, table types and sequences have no
  contract, no validator and no migration support.
- The runtime library has no test project of its own.
- The runtime applier and the migration parser disagree about `GO` separators and duplicate the resource
  loader; there is no single script catalog yet.
- `HasDifferences` is not overridden, so `dotnet ef migrations has-pending-model-changes` does not see
  procedure-only edits; `migrations script --idempotent` wraps `CREATE OR ALTER` in `IF ... BEGIN`, which
  SQL Server rejects.
- Parameter type mapping falls through to `varchar` for `real`, `float`, `money`, `datetimeoffset`, `xml`
  and others; `decimal` without an explicit scale truncates; `nvarchar` length validation compares
  characters against bytes.
- No drift detection against `sys.sql_modules`: a procedure altered in the database by hand is invisible.
