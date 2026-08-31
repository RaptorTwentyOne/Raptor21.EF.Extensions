# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the version is 0.x the public
surface is not a compatibility commitment.

## [Unreleased]

### Added

- `Raptor21.EF.Extensions.StoredProcedures` — the runtime: contracts, a contract-driven executor and a
  live-schema validator. Nothing in it deploys; deployment is EF's migration. Targets `net10.0`, marked
  `IsTrimmable` and `IsAotCompatible`; the trim and AOT analyzers report nothing on it.
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


- 512 tests across the three suites — 285 over the runtime library, 48 over the migration package, 179
  over the generator — and not one needs a SQL Server, a network or the file system. Ten pure statics
  sit behind the runtime library's `InternalsVisibleTo` to make that possible: eight widened from
  `private`, and two lifted out of loops that had been written inline. No existing public signature was
  widened to serve a test, and the trim and AOT analyzers still report nothing. The `GO` split is
  deliberately not among the ten — the migration package reads `GO` too, and an internal is a thing that
  package could not have called, so it is the public `SqlBatch` instead.
- `SqlBatch` — the one public reader of `GO` in this library, used by the migration parser and by
  anything a consumer writes against it.
- **Parameter facts now come from the procedure's own `.sql`, which makes `[Sql]` optional.** The
  generator parses the `CREATE`/`ALTER PROCEDURE` header of every script that reaches the compiler and
  fills in each parameter's SQL name, type, length, precision, scale and OUTPUT direction from the
  matching procedure parameter, so a declaration stops repeating what the procedure already states:

  ```csharp
  // Before: every fact written twice — once in ACCOUNT_LOGIN.sql, once here.
  [StoredProcedure("dbo.ACCOUNT_LOGIN")]
  public partial Task<int> LoginAsync(
      [Sql("@AccountID", 21)] string accountId,
      [Sql("@Password", 28)] string password,
      CancellationToken ct = default);

  // After: the .sql is the single source of both.
  [StoredProcedure("dbo.ACCOUNT_LOGIN")]
  public partial Task<int> LoginAsync(
      string accountId,
      string password,
      CancellationToken ct = default);
  ```

  A parameter is looked up as `@` plus its C# name unless `[Sql]` states one, matched case-insensitively;
  an inferred name is emitted with the file's own spelling, so a bare `string sku` looks up `@sku`,
  matches `@Sku` and emits `@Sku`. Matching is by name and only by name: a
  positional fallback that landed wrong would send a value to the wrong parameter, and the startup
  validator compares by position too, so nothing downstream could catch it. The merge is per facet rather
  than per parameter — what `[Sql]` states wins, what it leaves unstated comes from the procedure, and the
  .NET type stays the last resort for the SQL type. A declaration that spells every facet out therefore
  emits byte-for-byte what it emitted before, with or without a script in sight, which is what makes the
  upgrade safe for a codebase that already carries hundreds of legacy scripts.
- `.sql` scripts reach the compiler on their own in any project that references the package, which now
  ships `buildTransitive/Raptor21.EF.Extensions.StoredProcedures.targets`. It lists every
  `EmbeddedResource` whose extension is `.sql` a second time as an `AdditionalFiles` item. The two
  channels are not interchangeable and neither can serve the other: `EmbeddedResource` is what carries
  the bytes inside the assembly, which is where `RegisterStoredProcedures` reads them at model-building
  time to put each procedure into the migration, and `AdditionalFiles` is the only
  channel Roslyn gives an analyzer for file content — at generation time the assembly does not exist, and
  reading the disk is not an option because RS1035 makes `System.IO.File` a compile error in a generator
  project. Listing the file twice costs the output nothing: an `/additionalfile:` entry produces no
  assembly content at all. A script the targets file cannot see — declared as `None` or `Content`, or
  added to `EmbeddedResource` after the import has been evaluated — needs an explicit
  `<AdditionalFiles Include="..." />`. `dotnet pack` now fails if the targets file is missing, for the
  same reason it already fails on a missing analyzer: both are invisible until a consumer hits them.
- `R21SqlScriptDiscovery`, the opt-out. Set it to `false` in a consuming project and no `.sql` is handed
  to the compiler at all: nothing is inferred, the script-derived diagnostics SPG010–SPG020 have nothing
  left to report, and every parameter needs its `[Sql]` again — which keeps working forever. The one
  that still speaks is SPG014, and deliberately so: a parameter left without an attribute is the SPG003
  error it always was, and SPG014 is what tells the developer that the channel which would have answered
  it is switched off. The opt-out is there for the project that already spells every attribute out and
  would rather its build not read several hundred scripts, and for the one whose checked-in headers no
  longer describe what is deployed.
- Eleven diagnostics for what the script says, SPG010 through SPG020, and with them the library's first
  reports below `Error`. The script is evidence, not authority: each disagreement between the file and
  the code is reported and then decided in favour of the code, so a stale or hand-edited `.sql` cannot
  turn a compiling consumer into a wall of errors. The few that do stop a build are held to a rule
  stricter than their severity — each can fire only on a parameter carrying no `[Sql]` attribute at all,
  and such a parameter is already an SPG003 error today, before any of this existed. The script is
  allowed to rescue a declaration the old generator rejected; it is never allowed to reject one the old
  generator accepted. `<NoWarn>` takes them one at a time; `R21SqlScriptDiscovery` takes the whole
  channel out.
  - **SPG010** fires when `[Sql]` and the procedure both state a facet — SQL type, length, precision,
    scale or OUTPUT — and disagree. The attribute wins, so nothing about the build changes, but one of
    the two is wrong and a contract that disagrees with the deployed procedure fails startup validation.
  - **SPG011** fires when the procedure declares a parameter the method does not carry. The validator
    compares parameter counts and rejects such a contract at startup, even when the SQL parameter has a
    `DEFAULT`.
  - **SPG012** fires on a char- or binary-family procedure parameter written with no length argument.
    SQL Server reads a bare `varchar` as `varchar(1)`; inferring that would truncate every value to one
    character at the server and still pass validation, because `sys.parameters.max_length` really is 1.
    No length is taken and the parameter keeps the binding it already had.
  - **SPG013** fires when the type the procedure declares cannot bind the parameter's .NET type — a
    `varchar` against a `byte[]`, say. It is asserted only against a type the script supplied; an
    explicit `[Sql(TypeName)]` is the developer's call and is never second-guessed.
  - **SPG014** fires once per method when no script declaring its procedure reached the compiler and a
    parameter needed facts from one. It is what separates "you forgot `[Sql]`" from "your scripts never
    reached the compiler" — scripts declared in a `Directory.Build.targets` that runs too late, or owned
    by another project, are indistinguishable from a missing attribute otherwise.
  - **SPG015** fires when two `.sql` files declare the same procedure. Nothing is inferred for it,
    because either file could be the truth.
  - **SPG016** fires when the procedure declares the parameter with a construct that carries no SQL type
    this generator can bind — `sql_variant`, `timestamp`, `sysname`, a spatial type, a table-valued
    parameter, a `CURSOR` — and the parameter has no `[Sql]` to state one. The .NET type is not a
    fallback there. Everywhere else a .NET guess is merely unverified; here the procedure's own type is
    known to be one the runtime cannot bind, so emitting the guess would trade a build error for a boot
    failure. It stops the build, which it may: such a parameter is an SPG003 error today.
  - **SPG017** fires once per facet the script fills in on a parameter that does carry a `[Sql]`, and
    only when the emitted contract actually moved — a fill landing on the value the declaration already
    had says nothing and is not reported. It is the one report about the feature working rather than
    failing, which is why it is the quietest in the set: the contract now agrees with the procedure, but
    it changed without anyone editing the declaration, and that is exactly what the byte-identity rule
    exists to keep visible.
  - **SPG018** fires when the script declares a parameter OUTPUT, the parameter carries no `[Sql]` to
    decide the direction itself, and the method's return type has no element after the RETURN value to
    receive it — so the value the procedure writes back would be discarded in silence. It is SPG009's
    mirror and cannot be folded into it: SPG009 counts a mismatch for a method that returns its OUTPUT
    parameters, this one catches the method that has nowhere to put one. It too stops the build, on the
    same ground as SPG016.

- Row types can be generated from the EF Core model: `[SqlRow(Entity = typeof(Product))]` on a partial
  record takes its members, their CLR types, their nullability and their column names from the
  `ModelSnapshot` in the same compilation. The generator reads the snapshot as ordinary C# syntax -
  it references no EF assembly, executes nothing and opens no connection - which is why it works in
  a CI with no database. Writing the record out by hand is unchanged and stays supported.
- Thirteen diagnostics for that binding, SPG021 through SPG033, all errors and all unreachable unless
  you write `Entity =`. They exist because the binding refuses rather than guesses: a table mapped by
  more than one entity type is declined outright, since under TPH - EF's default inheritance strategy
  - a derived type's non-nullable property is mapped to a nullable column, and a row built from the
  property would throw on the first sibling row. Inheritance, owned types, table splitting, value
  converters, a missing snapshot and two disagreeing snapshots are all refusals for the same reason.
  The one case that cannot be refused, because the snapshot records nothing about it, is a value
  converter whose provider type equals its CLR type.

- **A generated call can run on a connection and a transaction it did not create.** Until now it could
  not: `ISqlConnectionProvider.Create()` returned a fresh, unopened connection that the emitted body
  disposed after one call, and `IStoredProcedureExecutor` had no transaction anywhere on its surface — so
  a procedure could not join a caller's unit of work, and SQL Server rejects a command outright on a
  connection whose local transaction is pending but whose `Transaction` is unset.

  The new `SqlConnectionLease` is that decision, as one value: a connection, the transaction it is inside,
  and the release handle disposal runs. `ISqlConnectionProvider` gains one **default-implemented** member,
  `LeaseAsync`, whose body is exactly what every call did before — `Create()`, open, own — so every
  provider that exists keeps compiling and keeps behaving identically. A provider that borrows overrides
  it and returns `SqlConnectionLease.Borrow(connection, transaction, release)`:

  ```csharp
  public ValueTask<SqlConnectionLease> LeaseAsync(CancellationToken ct = default) =>
      new(SqlConnectionLease.Borrow(unitOfWork.Connection, unitOfWork.Transaction));
  ```

  A borrowed connection is never opened, never closed and never disposed by this library. `Borrow` is the
  one place the abstract `DbConnection`/`DbTransaction` are narrowed — an ORM hands those back — and it
  refuses a non-SqlClient provider by name rather than letting an `InvalidCastException` surface inside
  the parameter loop, checks that the transaction belongs to the connection, and refuses one that has
  already completed. Nothing in the emitted body or the executor branches on ownership: there is one
  connection statement per generated method and one `cmd.Transaction = lease.Transaction` in the executor,
  and neither tests anything.

  No new dependency, no reflection, and no Entity Framework: the adapter that pairs EF's reference-counted
  open with a matching close belongs on the consumer's side, which is why `Borrow` takes a release handle
  instead of knowing about `DbContext`.

### Changed

- Extracted from the KnightOnline repository, where this lived as `SqlServerStoredProcedureSchema`.
  Namespaces, assembly names and package ids were renamed to the `Raptor21.EF.Extensions` family; the
  EF integration dropped the redundant `.EntityFrameworkCore` suffix in favour of `.Migrations`.
  Nothing else about the code changed in the move: the same 25 tests pass before and after.
- Dropped the KnightOnline root `Directory.Build.props`, which injected a `BuildInfo.Versions` type into
  every assembly it built — including, previously, the shipped ones.


- **The supported consumer is a code-first EF Core project.** That is a decision about who this library
  is for, not something the code enforces today, and the difference is worth stating plainly: nothing in
  the runtime or the generator references Entity Framework at all — the runtime package's single
  dependency is `Microsoft.Data.SqlClient`. What the decision rules out is a branch, not a build: an
  EF-free fork will not be supported. The generator already needs a model — `[SqlRow(Entity = ...)]`
  reads the `ModelSnapshot` — so read the EF-free *runtime* as the layering it is rather than as a
  promise that a model will never be required.
- **Breaking: `net8.0` is gone; everything targets `net10.0`.** `Raptor21.EF.Extensions.Migrations`
  carries EF Core 10, which ships `net10.0` alone, and the two packages are meant to be installed
  together by the consumer described above — so a `net8.0` asset of the runtime would only serve someone
  this project has already decided not to serve. CI installs one SDK.
- `Raptor21.EF.Extensions.Migrations` now depends on `Raptor21.EF.Extensions.StoredProcedures`. The two
  halves had drifted apart on `GO`; the rule now lives in `SqlBatch` and both call it. The direction is
  the honest one — the EF integration sits on top of the EF-free runtime, never the reverse.
- **Breaking: an unmapped SQL type now throws instead of binding as `varchar`.** `real`, `float`,
  `money`, `smallmoney`, `text`, `ntext`, `image`, `xml` and `smalldatetime` gained correct mappings;
  what remains genuinely unmapped (`sql_variant`, `timestamp`, spatial types, table types) raises an
  exception naming the parameter and the type. Silent `varchar` was data corruption that the validator
  green-lit, and `real`/`float` were the default path for every `float` and `double` parameter.
- **Breaking: `datetimeoffset` and `time` bind to their own types** rather than `datetime2`, so an
  offset is no longer discarded on the way to the server and a `time` no longer acquires a date part.
- **Breaking: the validator's type map is no longer inverted.** `datetimeoffset` columns are accepted;
  `date`, `datetime`, `datetime2` and `smalldatetime` no longer accept `DateTimeOffset`; `time` requires
  `TimeSpan` rather than admitting a `DateTime` the generated reader cannot materialise.
- **Breaking: `nvarchar` length validation compares like with like.** The catalog reports bytes and the
  contract counts characters, so every correctly declared `nvarchar(n)` parameter used to fail at boot.
- **Breaking: two embedded resources yielding one script name now throw**, naming both, instead of one
  silently overwriting the other and never running.
- **Breaking: batches are joined with `
`, not `Environment.NewLine`**, and lines keep their trailing
  whitespace. One `.sql` used to produce different SQL — and a different hash — from a Windows deploy
  than from Linux CI.
- Argument guards run before any `SqlCommand` is built, and `ArgumentNullException` now names the
  parameter the caller actually wrote. Validator messages render a nullable value type as `Int32?`
  rather than ``Nullable`1``.
- SPG003, SPG005 and SPG009 are now decided after the script has been consulted, and their messages say
  so. A parameter without `[Sql]` is no longer wrong on sight: it is an error only once the procedure has
  also failed to supply a name (SPG003) or a type (SPG005), and OUTPUT arity (SPG009) is counted against
  the directions actually in force rather than against the attributes alone.
- `[Sql]` gained a parameterless constructor and a settable `Name`, so a single facet can be overridden
  and the rest left to the script — `[Sql(Length = 8000)]` on a parameter whose procedure header says
  `varchar` and nothing more.
- **Breaking: `StoredProcedureDiff.Compute` now returns a wrapped script for `ProcOpKind.CreateOrAlter`.**
  `ProcOp.Sql` is `EXEC(N'<escaped script>');`, not the script as written, so a direct caller — `Compute`,
  `ProcOp` and `ProcOpKind` are all public — gets different SQL out of unchanged code of its own. Read the
  value as the statement to run rather than as the script that was handed in: the wrap is
  `StoredProcedureScript.WrapInExec`, which is public for exactly that reason, and it is undone by
  stripping the `EXEC(N'` and `');` and halving every doubled quote — recovering the script but for the
  trailing `;` and newline the wrap trims off. `ProcOpKind.Drop` is untouched. The wrap lives in the diff
  rather than in a custom `IMigrationsSqlGenerator` so that the consumer keeps a single `ReplaceService`
  — a second one, forgotten, would silently reproduce the defect it exists to fix — and the visible cost
  is that a scaffolded migration now reads `EXEC(N'...')` instead of the bare script.
- **Breaking for implementers: `IStoredProcedureExecutor`'s five methods take a `SqlConnectionLease`
  where they took a `SqlConnection`.** The parameter count is unchanged and so is everything else on the
  surface. It is a lease rather than a sixth `SqlTransaction?` parameter because the connection and the
  transaction cannot be read apart — a command whose transaction belongs to another connection is
  refused — and because a future execute path can forward a connection and forget a transaction, while
  it cannot forward half a value. Nothing was published when this landed, so no consumer's implementation
  of the interface exists to break; a generated call site is unaffected either way, because the generator
  writes it.
- The emitted method body's two-statement prologue is now one statement,
  `await using var __lease = await __connectionProvider.LeaseAsync(ct).ConfigureAwait(false);`, and the
  executor call's first argument is `__lease` rather than `__conn`. The behaviour is identical for a
  consumer who does not borrow, statement for statement: the same `Create()`, the same `OpenAsync` with
  the same token, the same disposal at scope exit — including disposal after a failed open, which the old
  `await using` gave for free and `OpenOwnedAsync` reproduces in a `catch`. The generated class
  constructor keeps its exact two-parameter shape, so no DI registration moves.
- The generated constructor now carries an XML doc comment. `// <auto-generated/>` does not suppress
  CS1591, and the constructor is the one generated member that owes one — the partial methods' comments
  live on the developer's own declaring part — so a consumer building with `GenerateDocumentationFile`
  and no CS1591 suppression was collecting one warning per group class in a file they cannot edit.

### Removed

- **`EmbeddedScriptApplier` and `StoredProcedureSchemaManager` are gone, and with them
  `dbo.__DbScriptHistory`.** The library shipped two ways to get a procedure into a database: an EF
  migration, and a runtime applier that scanned embedded `.sql` resources at boot, split them on `GO`,
  hashed each one and recorded it in a history table of its own. The applier came in with the extraction
  from the original repository — `docs/AUDIT-AND-ROADMAP.md` lists it as inherited — and predates the
  ruling that this is a code-first EF library. Nothing forced the two to meet, so it survived.

  Two deployment mechanisms over one set of files is one too many. They kept separate books —
  `__EFMigrationsHistory` against `__DbScriptHistory` — and neither could read the other's, so a
  procedure could be applied by both, and the two could disagree about what was deployed with nothing in
  either to notice. The applier also opened its own connection from its own connection string, which is
  why it could never join a caller's transaction.

  The proximate reason is smaller and sharper. The round before this one fixed the applier's apply order,
  which was `Assembly.GetManifestResourceNames()` order — unspecified, and collapsing to hash order once
  six script names share a length. The fix was to invent a deployment ordering rule: sort by script name,
  ordinal and case-insensitive, with "zero-pad your numbers or `10_x` runs before `9_x`" written into the
  public doc comment. Migrations already solve that problem with an ordered, named, recorded list.
  Writing a second, weaker answer to a question EF had answered is what made the duplication visible.

  What replaces it: nothing, deliberately. `dotnet ef migrations add` carries procedures already, because
  `RegisterStoredProcedures` puts every `.sql` on the model and `StoredProcedureModelDiffer` diffs them.
  What survives untouched is the half that reads rather than writes —
  `StoredProcedureValidator.ValidateAsync`, which compares each generated contract to `sys.parameters`
  and the described first result set and throws at boot on a mismatch. It was the useful half of
  `ApplyAndValidateAsync` and it is now called directly; the sample's step 2 shows it. `SqlBatch` also
  survives: the migration parser is a caller, so the `GO` reader was never the applier's.

  What a consumer feels: `EmbeddedScriptApplier.ApplyEmbeddedScriptsAsync` and
  `StoredProcedureSchemaManager.ApplyAndValidateAsync` no longer exist. Replace the latter with
  `StoredProcedureValidator.ValidateAsync(connectionString, contracts, ct)` and let the migration deploy.
  A database that carries a `dbo.__DbScriptHistory` table from an earlier version is not read, not
  written and not dropped — it is inert, and dropping it is a decision for whoever owns that database,
  since its rows are the only record of what the old path applied.
- The `decimal` scale-truncation gap, and the apply-order gap that replaced it in the previous round.
  Both are settled below rather than open: the first was withdrawn on evidence, the second no longer has
  a mechanism to be about.

### Fixed

- The shipped analyzer loads on every .NET 10 SDK again. Its `Microsoft.CodeAnalysis.CSharp` reference
  had moved to the newest release with the rest of the tree, and that line is not a dependency version —
  it is the oldest compiler that can load the generator. A compiler older than the Roslyn it was built
  against refuses the assembly with CS9057, which is a *warning*; every contract, row materialiser and
  registry the generator writes is then absent, and the build fails on missing types and partial methods
  with no implementation, in the consumer's own files, naming no analyzer. Reproduced rather than
  reasoned about: the sample built against Roslyn 5.9 fails on SDK 10.0.100 with CS9057 and a dozen
  CS0234/CS8795, and builds clean against 5.0.0 on the same SDK. Pinned to 5.0.0, the Roslyn in the first
  .NET 10 SDK — the library targets `net10.0` alone, so that floor covers every SDK that can build a
  consumer. CI would never have caught it: both workflows use `dotnet-version: 10.0.x`, which resolves to
  the newest patch.
- Every other package moved to its current release: EF Core 10.0.11 across the shipping migration package,
  the sample and the tests; `Microsoft.NET.Test.Sdk` 18.9.0 and `xunit.runner.visualstudio` 4.0.0 in the
  three test projects; Roslyn 5.9.0 in the generator's own test project, which is not shipped and is
  deliberately ahead of the floor above.
- `RegisterStoredProcedures` refuses two embedded scripts that declare the same procedure, naming both
  files, instead of letting the second silently overwrite the first. The annotation key is the parsed
  qualified name and not the file name, so `010_Upsert.sql` and `020_Upsert_v2.sql` both declaring
  `dbo.usp_Upsert` used to leave one of them in no migration at all — a file plainly in the repository,
  plainly an `EmbeddedResource`, and simply absent, surfacing far away as a procedure that never changes
  no matter how it is edited. `usp_X` and `dbo.usp_X` collide too, since an unqualified name is `dbo`.
  The guard came from the removed applier, which had it; this is now the library's only loader, so
  nothing else was going to catch it.
- `GO` inside a string literal, a bracketed identifier or a nested block comment is no longer treated as
  a batch separator, and an indented `GO` now is one. Both readers agree by construction.
- `GO` inside a `"..."` construct is content too, and the fix runs in both directions. A `"..."` is a
  delimited identifier under `QUOTED_IDENTIFIER ON` — SqlClient's default — and a string literal with it
  off; the scanner cannot know which, since the setting can be changed by a batch it has already handed
  on, and it does not need to, because under both readings the contents are not statement text and `""`
  is the escape. The direction the known-gaps entry described was the milder one: a line reading `GO`
  inside a multi-line `"..."` used to cut the construct in half. The direction it missed is worse — an
  apostrophe, a `[` or a `/*` written inside a `"..."` used to open a construct that never closed, and
  the next genuine `GO` was then swallowed and two batches merged into one, which is exactly how
  `CREATE OR ALTER PROCEDURE` stops being the first statement in its batch. On the migration side the
  same miss let `ParseQualifiedName` accept a multi-batch script it exists to refuse. What a consumer
  feels, and it is a visible break: a script containing a legal `"it's"` or `"a[b"` followed by a real
  `GO` used to register and now throws "contains a 'GO' batch separator" at registration, trading a
  deployment-time failure for a build-time one; and the same script is now sent to the server as two
  batches where it used to arrive merged.
- Leading blank lines of a batch survive, so SQL Server's reported line numbers match the `.sql` source.
- A resource whose stream comes back null is reported by name instead of as
  `ArgumentNullException("stream")`.
- `dotnet pack` of the generator used to fail with NU5017 (`IncludeBuildOutput=false` and nothing to
  pack). The generator is no longer its own package, so the failure is gone by construction.
- `dotnet ef migrations has-pending-model-changes` sees a procedure-only edit, because
  `StoredProcedureModelDiffer` now overrides `HasDifferences` as well as `GetDifferences`. EF reaches its
  two answers down separate paths — `HasDifferences` stops at the first difference the protected `Diff`
  produces, `GetDifferences` sorts everything `Diff` produced — so overriding the second alone left the
  command answering the wrong question: it reported a model as up to date while the very next
  `migrations add` would have written a migration, and that command is what teams gate CI on. The same
  blindness silenced the `PendingModelChangesWarning` that `Migrate()` raises and let `migrations remove`
  treat a procedure-only migration as one it could revert. The procedure verdict is ORed onto EF's own,
  never substituted for it, so a table-only change still reports `true`; and both entry points reach it
  through one private helper, because a `HasDifferences` that disagreed with the differ would be worse
  than the gap it closes — the command would answer "no changes" and the migration EF then scaffolds
  would contradict it.
- `dotnet ef migrations script --idempotent` produces valid T-SQL. `CREATE OR ALTER PROCEDURE` must be
  the first statement in its batch and is first in none of the scripts EF writes: `--idempotent` puts
  `IF NOT EXISTS (SELECT ... FROM [__EFMigrationsHistory]) BEGIN` in front of every command, and even the
  plain `migrations script` opens a `BEGIN TRANSACTION` and then separates commands with a newline rather
  than a batch terminator, so SQL Server rejected both. The procedure is now emitted through
  `EXEC(N'...')`, which compiles its argument as a batch of its own and so is legal in either — and
  unconditionally, because gating the wrap on the idempotent flag, the way EF's own generator gates
  `GenerateExecWhenIdempotent`, would have left the plain script exactly as invalid as it was. The
  escaping is one `N'...'` literal with every single quote doubled: doubling escapes a character rather
  than re-parsing a construct, so a body that already contains `''` survives being doubled again, and
  inside a string literal `--`, `/* */` and a line reading `GO` are all merely characters. It is
  deliberately not a `'...' + '...'` concatenation, which types each piece on its own and so reintroduces
  the 4,000-character cliff a single literal is promoted past — a body that deploys truncated is a far
  worse failure than one that refuses to parse. Both fixes are pinned by tests that drive EF's real SQL
  Server provider, through `IMigrator.GenerateScript` and `IRelationalModel`, and neither opens a
  connection: CI still needs no database.

### Known gaps

Most are carried over from the audit that preceded this extraction; the ones this release introduced
are marked NEW. Two of those are the price of wrapping a procedure in `EXEC(N'...')` so that idempotent
scripts parse at all, and both are stated with the paths they do and do not affect. One entry is marked
WITHDRAWN: it was taken up as a defect, driven, and found not to be one. It stays on the list, rewritten,
because a claim this list makes and nobody re-checks is how a list like this stops being read at all —
and because the withdrawn one told consumers to write explicit scales into their contracts to prevent a
truncation that does not happen.

- NEW, and a consequence of the `EXEC(N'...')` wrap: `migrations script --idempotent` indents every
  line of a command by four spaces, and because the procedure body now lives inside the literal, that
  indentation is executed as part of the string. The procedure runs correctly, but the text SQL Server
  stores in `sys.sql_modules.definition` is not byte-identical to the `.sql` file — which will matter
  to the drift check that is still on the list, and shifts the line numbers in a runtime error by the
  indent. `dotnet ef database update` is unaffected; only the generated script indents.
- NEW, same cause: a procedure body may legally contain a line reading `GO` inside a string literal —
  `SqlBatch` correctly treats it as content, and `database update` runs it. In a generated script
  that line ends up inside the `EXEC(N'...')` literal, and `sqlcmd`'s batch scanner is line-based and
  does not know it is inside a literal, so it cuts there and the deployment fails with a syntax error.
  Loud rather than silent, and only on the `sqlcmd` path — `database update` and SSMS are unaffected.
  Refusing such a body outright was tried and rejected: it removes a capability that works on every
  other path, and re-splits the `GO` rule that this release just unified.
- Only stored procedures are first-class. Functions, views, triggers, table types and sequences have no
  contract, no validator and no migration support.
- WITHDRAWN, and left here because a wrong entry is worth correcting in public: `decimal` declared
  without an explicit scale was said to default the scale to 0 and truncate. It does not. A zero scale is
  never put on the wire — SqlClient serialises a parameter's scale only when it is non-zero — so a
  contract that omits the scale and a contract that states an explicit `0` both send the value's own
  scale, exactly as an untouched `SqlParameter` does; `ApplySqlType`'s `spec.Scale ?? 0` is inert in the
  zero case rather than lossy. What does round a value is SQL Server's own rule at the server: a
  procedure declaring a bare `decimal` parameter has declared `decimal(18,0)`, and the value is rounded
  on assignment to it. That is the procedure's declaration to fix, and the generator now reads the scale
  out of the header when the `.sql` reaches the compiler. Driven against Microsoft.Data.SqlClient 7.0.2,
  whose `ShouldSerializeScale` is `_scale != 0` on both its branches; a future provider that honoured an
  explicit zero would make that `?? 0` live, which is the one thing worth watching here.
- The precision half of the same story is real, and it fails in the opposite direction from the entry
  above. A contract stating no precision is given 18 by `ApplySqlType`, and unlike a zero scale a
  non-zero precision IS transmitted — so a value needing more than 18 digits is refused by the provider
  before it reaches a procedure declared `decimal(38,4)` that could have held it. The neighbouring
  datetime branch already takes the other decision: `ApplyFractionalSecondsScale` sets a scale only when
  the contract states one, on the ground that saying nothing is the correct answer rather than a gap.
  Leaving the precision unstated would change what the server is told for every undecorated `decimal`
  parameter of every consumer, which is why it is written down rather than changed quietly, and pinned by
  a test that contrasts the mapped parameter with an untouched one.
- A contract that states a precision and no scale is emitted with `(byte)0` for the scale, and
  `StoredProcedureValidator` compares a scale it is given: such a contract fails startup validation
  against a `decimal(18,2)` procedure with "scale mismatch. Expected 0, found 2" — while, per the first
  entry above, it would have bound the value correctly had it been allowed to run. Only reachable with
  `R21SqlScriptDiscovery=false` or with no script in the compilation; with the `.sql` present the merge
  fills the scale from the header. `SqlTypeSpec.Scale` is `byte?`, so emitting `null` there is
  expressible and would leave wire behaviour byte-identical while removing the false failure — but it
  changes the generator's emitted text for that one spelling, which is precisely the byte-identity
  guarantee `RenderSqlType` was written to protect, so it is the owner's call rather than an obvious fix.
- Script order is deterministic now, not correct. Nothing reads a script to discover that it needs
  another, so the order comes from the names and is only as good as the naming: the comparison is
  lexicographic rather than numeric, so an unpadded `10_x.sql` is applied before `9_x.sql`, and a corpus
  whose intended order disagrees with its alphabet — one whose author ran `RollupSessions` before
  `RollupDaily` — is now reliably applied the other way instead of unreliably applied either way.
  Encoding a dependency in the file names stays the consumer's job.
- The batch scanner still refuses `GO 5`, `GO;` and `GO -- done` as separators, though `sqlcmd` accepts
  all three, and it splits on `\n` alone, so a classic-Mac file with `\r` line endings is one enormous
  batch. Both are pinned by tests as decisions rather than reported here as bugs, because both halves of
  the library are blind in the identical way and a file is therefore never accepted by one and cut by the
  other; changing either is a product decision.
- Whether the generator reaches a project that gets this package only transitively is the SDK's
  behaviour, not ours, and it is not what the packaging suggests. The `Migrations` package records the
  dependency with `exclude="Build,Analyzers"`, yet on SDK 10.0.400 a three-package chain measured here
  - this package, a library referencing it, an app referencing only that library - still ran the
  generator in the app and emitted its contracts. Do not rely on either answer: a project that declares
  procedures should reference this package directly, and that instruction is about being independent of
  the SDK rather than about a limitation this package has. Note the shape of the risk if the flow ever
  stops: `buildTransitive` targets travel where analyzers might not, so a downstream project could end
  up handing its `.sql` `EmbeddedResource` items to a generator that is not there. That is the reason
  to name this package directly rather than to reason about what NuGet will do.
- Borrowing closes the gap for generated CALLS and not for validation. `StoredProcedureValidator` still
  takes a connection string and opens a connection of its own, so it cannot run inside a caller's
  transaction. "The library is transaction-aware now" is half true. Smaller than it was: the two other
  types this entry used to name deployed, and they are gone.
- `cmd.Transaction = lease.Transaction` with a NON-null transaction cannot be unit-tested: `SqlTransaction`
  has no public constructor and cannot be obtained without a server. The offline suite proves the null
  case only; the borrowing path itself is proved by the sample's rollback step, which needs
  `SAMPLE_SQL_CONNECTION`. A regression in the one line the capability rests on is invisible to a bare
  `dotnet test`.
- The default `LeaseAsync` makes the fallback silent by design: a provider written to borrow but never
  overriding it gets a fresh connection outside the transaction and looks like it worked. A borrowing
  provider should throw from `Create()` so a half-written adapter fails at the first call instead.
- `SqlConnectionLease` is a struct, so a caller who copies one and disposes both copies runs the release
  handle twice. The generated body uses a single `await using` and cannot; a hand-written release handle
  should be idempotent anyway, because one close too many on a borrowed connection ends the caller's
  transaction and the failure surfaces nowhere near its cause.
- No drift detection against `sys.sql_modules`: a procedure altered in the database by hand is invisible.
