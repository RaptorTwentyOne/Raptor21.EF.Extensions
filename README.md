# Raptor21.EF.Extensions

Code-first SQL Server database objects for EF Core. Procedures live in your repository, are called
through a compile-time contract, and travel in the same migration as the tables they touch — with no
runtime reflection, so a consumer can still publish AOT.

Entity Framework gives you code-first tables. It gives you nothing for the rest of the database, which
is how procedure bodies end up living only in production, versioned by whoever last opened SSMS. That is
the gap this family closes.

| Package | What it does |
|---|---|
| **`Raptor21.EF.Extensions.StoredProcedures`** | The runtime — contracts, a contract-driven executor and a live-schema validator — plus the Roslyn source generator, which ships inside this package. |
| **`Raptor21.EF.Extensions.Migrations`** | Registers those scripts on the EF model and diffs them, so `dotnet ef migrations add` emits `CREATE OR ALTER` / `DROP` alongside the table DDL. Carries partition functions and schemes, a table's placement on a scheme and clustered columnstore indexes the same way. EF Core 10, SQL Server. |

The runtime and the generator are separate projects on purpose: schema knowledge is resolved at compile
time and never by reflection, which is what keeps trimming and AOT working. They are *not* separate
packages — the generator is packed under `analyzers/dotnet/cs`, so one reference brings both and the two
can never drift apart in version.

A complete worked example lives in
[`samples/Raptor21.EF.Extensions.Sample`](samples/Raptor21.EF.Extensions.Sample): one table and four
procedures, migrated together, validated at startup, called through the generated API.

---

## Quick start (with the generator)

```xml
<PackageReference Include="Raptor21.EF.Extensions.StoredProcedures" Version="0.1.0-preview.1" />
<!-- Only if you want procedures inside EF Core migrations: -->
<PackageReference Include="Raptor21.EF.Extensions.Migrations" Version="0.1.0-preview.1" />
```

The generator arrives with the first package, and so does the build logic that shows it your `.sql`
files. There is nothing else to wire up.

Declare a group. A `[StoredProcedure]` partial method is all you write — the parameters carry no
attributes, because the generator reads their SQL names, types and lengths out of the procedure's own
`CREATE PROCEDURE` header at compile time:

```csharp
using Raptor21.EF.Extensions.StoredProcedures.Generated;

[StoredProcedureGroup]
public partial class AccountProcedures
{
    // RETURN value (SQL Server RETURN is always int; short is also supported)
    [StoredProcedure("dbo.ACCOUNT_LOGIN")]
    public partial Task<int> LoginAsync(
        string accountId,          // matched to @AccountID varchar(21) in ACCOUNT_LOGIN.sql
        string password,           // matched to @Password  varchar(28)
        CancellationToken ct = default);

    // Result set -> typed rows
    [StoredProcedure("dbo.GET_CHARACTER_LIST")]
    public partial Task<IReadOnlyList<CharRow>> GetCharactersAsync(
        string accountId,
        CancellationToken ct = default);

    // Result set + RETURN value
    [StoredProcedure("dbo.GET_CHARACTER_LIST2")]
    public partial Task<(int ReturnValue, IReadOnlyList<CharRow> Rows)> GetCharactersWithReturnAsync(
        string accountId,
        CancellationToken ct = default);

    // No RETURN / result set consumed
    [StoredProcedure("dbo.TOUCH_LAST_LOGIN")]
    public partial Task TouchLastLoginAsync(
        string accountId,
        CancellationToken ct = default);
}

[SqlRow]
public partial record CharRow(
    [SqlColumn("CharID")] int CharId,   // column name override
    string Name,                        // column "Name"
    int? Level);                        // nullable -> IsDBNull-aware read
```

The generator emits the constructor `(ISqlConnectionProvider, IStoredProcedureExecutor)`, the
`IStoredProcedureContract` per procedure, the `FromDataRecord` for each `[SqlRow]`, and a
`<RootNamespace>.Generated.GeneratedProcedureRegistry.All` listing every contract.

The call site is fully type-checked — wrong argument types or counts won't compile:

```csharp
var ret = await accountProcedures.LoginAsync(accountId, password, ct);
```

### Where the parameter facts come from

Every `.sql` you already ship as an `EmbeddedResource` is handed to the compiler a second time as an
`AdditionalFiles` item — the package's `buildTransitive` targets do that for you, so a
`PackageReference` is the whole setup. The generator parses each `CREATE`/`ALTER PROCEDURE` header and
matches a C# parameter to the SQL parameter named `@` + its name, ignoring case; an inferred name is
emitted with the procedure's own spelling, so `string sku` looks up `@sku`, matches `@Sku` and emits
`@Sku`. Matching is by name only — there is no positional fallback,
because one that landed wrong would send a value to the wrong parameter, and the startup validator,
which compares by position too, could not catch it.

The merge is per facet, not per parameter:

| Facet | First | Then | Last resort |
|---|---|---|---|
| SQL name | `[Sql("@x")]` | the procedure parameter named `@` + the C# name | — (**SPG003**) |
| SQL type | `[Sql]` | the procedure | the .NET type (**SPG005** if none) |
| length, precision, scale | `[Sql]` | the procedure | left unspecified |
| `Output` | any `[Sql]` at all decides it | the procedure | `false` |

A declaration that spells every facet out therefore generates exactly what it generated before, with or
without a script in sight: upgrading cannot break a build that compiles today.

Scripts declared as `None` or `Content`, or added to `EmbeddedResource` after the package's targets are
imported, are not discovered — **SPG014** says so — and need an explicit
`<AdditionalFiles Include="DbScripts\*.sql" />`. Set
`<R21SqlScriptDiscovery>false</R21SqlScriptDiscovery>` to switch discovery off entirely: no script is
read, nothing is inferred, the script-derived diagnostics SPG010–SPG020 have nothing left to report, and
every parameter needs its `[Sql]` again, exactly as before. SPG014 is the one that still speaks, on
purpose — a parameter left without an attribute is an SPG003 error either way, and SPG014 is what tells
you the channel that would have answered it is off rather than empty.

### Dependency injection

```csharp
services.AddSingleton<DbConnectionFactory>();                 // implements ISqlConnectionProvider
services.AddSingleton<ISqlConnectionProvider>(sp => sp.GetRequiredService<DbConnectionFactory>());
services.AddSingleton<IStoredProcedureExecutor, StoredProcedureExecutor>();
services.AddSingleton<AccountProcedures>();
```

The executor is stateless — every method takes its connection as an argument — so it is a singleton
whatever else you do. The provider and the group class share one lifetime: a provider that hands out a
per-request connection must be scoped, and the group class that holds it must be scoped too, or the
container refuses it as a captive dependency.

### Joining a caller's transaction

A generated call runs on whatever its `ISqlConnectionProvider` gives it. The default gives it a fresh
connection it opens and disposes for the one call, which is the shape above. A provider that overrides
`LeaseAsync` gives it a connection somebody else owns — an ORM's, a unit of work's — and the transaction
that connection is inside:

```csharp
public sealed class UnitOfWorkConnectionProvider(IUnitOfWork unitOfWork) : ISqlConnectionProvider
{
    // Never reached: generated bodies call LeaseAsync. Throwing says so rather than quietly
    // handing back the second connection this type exists to avoid.
    public SqlConnection Create() => throw new NotSupportedException("This provider borrows.");

    public ValueTask<SqlConnectionLease> LeaseAsync(CancellationToken ct = default) =>
        new(SqlConnectionLease.Borrow(unitOfWork.Connection, unitOfWork.Transaction));
}
```

`Borrow` takes the abstract `DbConnection`/`DbTransaction`, because that is what an ORM hands back, and
narrows once — naming the offending provider if it is not SqlClient's, since parameter binding sets
`SqlDbType` and no other provider has one. The borrowed connection is never opened, never closed and
never disposed by this library; pass an `IAsyncDisposable` as `Borrow`'s third argument when the provider
itself had to open something, and the lease runs it exactly once. Nothing in the generated body branches
on any of this, so registering the default provider keeps exactly the behaviour it has today.

`ValidateAsync` is not part of this: it still takes a connection string and opens a connection of its
own.

### Startup: validate contracts against the live DB

```csharp
await StoredProcedureValidator.ValidateAsync(
    connectionString,
    MyApp.Generated.GeneratedProcedureRegistry.All,       // generated; no manual list
    ct);
```

`ValidateAsync` compares each contract to the real procedure (`sys.parameters`,
`sys.dm_exec_describe_first_result_set_for_object`) and throws on the first mismatch in
parameter names/types/lengths or result-set columns. Fail fast at boot.

It reads and never writes. **Nothing in this library deploys a procedure except an EF migration** — there
is no second applier, no second history table and no way for a startup path and a migration to disagree
about what is in the database. If validation fails, the answer is a migration, not a repair at boot.

---

## EF Core migrations

Add `Raptor21.EF.Extensions.Migrations`, register the scripts on the model, and replace the differ:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.Entity<Product>(/* ... your tables, as usual ... */);

    // Every .sql under the given resource prefix becomes a model annotation.
    modelBuilder.RegisterStoredProcedures(typeof(CatalogDbContext).Assembly, "MyApp.DbScripts");
}
```

```csharp
new DbContextOptionsBuilder<CatalogDbContext>()
    .UseSqlServer(connectionString)
    .ReplaceService<IMigrationsModelDiffer, StoredProcedureModelDiffer>()   // without this, procedures
    .Options;                                                               // are silently left out
```

That single replacement is enough for procedures. A context that also places tables on partition schemes
calls `.UseCodeFirstDatabaseObjects()` instead, which installs the differ and the SQL generator together —
see [Partitioning and columnstore](#partitioning-and-columnstore).

`dotnet ef migrations add Initial` then produces one migration containing the table DDL **and** a
`CREATE OR ALTER` per procedure, with `DROP PROCEDURE IF EXISTS` in `Down`. Change a procedure body and
the next migration carries only that change; the annotations round-trip through the model snapshot, so
the diff is against what was last migrated, not against the database.

Each script must be a single `CREATE OR ALTER PROCEDURE` batch — no `GO` separators, and `CREATE OR
ALTER` rather than plain `CREATE`, because idempotency is what makes re-running a migration safe.

`dotnet ef migrations has-pending-model-changes` notices a procedure-only edit. EF reaches the differ
through two entry points, and `StoredProcedureModelDiffer` overrides both: overriding only the one
`migrations add` uses left that command reporting a model as up to date while the next `migrations add`
would in fact have written a migration — which is the wrong answer to gate CI on. The same override is
what raises `PendingModelChangesWarning` from `Migrate()` when only a procedure moved.

`migrations script --idempotent` produces valid T-SQL (with two caveats — see the CHANGELOG's known
gaps: the generated script indents the body inside the literal, and a body containing a line reading `GO`
inside a string literal is cut by `sqlcmd`). `CREATE OR ALTER PROCEDURE` has to be the first
statement in its batch, so a procedure travels inside `EXEC(N'...')` and executes as a nested batch of its
own: every single quote in the body is doubled, and the body is emitted as one literal rather than a
concatenation, which would reintroduce a truncation limit that a single `N'...'` constant is promoted
past. The wrap is unconditional, because plain `migrations script` is no safer — it opens a
`BEGIN TRANSACTION` and separates commands with a newline rather than a batch terminator. The visible
cost is that the scaffolded migration and the generated script read `EXEC(N'...')` around the procedure
instead of the bare body. `DROP PROCEDURE IF EXISTS` is bound by no such rule and is left unwrapped.

---

## Partitioning and columnstore

The same mechanism carries a table's physical layout. A partition function, a partition scheme, a table's
placement on that scheme and a clustered columnstore index are each an annotation on the model, so they
travel through the snapshot, are diffed against it, and land in the migration next to the table they belong
to — in the order SQL Server needs them.

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.HasPartitionFunction(
        "pf_EventsMonth", "datetime2(3)", PartitionRange.Right,
        PartitionBoundaries.Monthly(new DateOnly(2026, 8, 1), count: 12));  // '20260801' … '20270701'
    modelBuilder.HasPartitionScheme("ps_Events", "pf_EventsMonth");         // ALL TO ([PRIMARY])

    modelBuilder.Entity<Event>(b =>
    {
        b.HasKey(e => new { e.Id, e.OccurredAt }).IsClustered(false);  // the columnstore is the clustered index
        b.OnPartitionScheme("ps_Events", e => e.OccurredAt);            // CREATE TABLE … ON [ps_Events]([OccurredAt])
        b.HasClusteredColumnstoreIndex("cci_Events");
    });
}
```

```csharp
new DbContextOptionsBuilder<EventsDbContext>()
    .UseSqlServer(connectionString)
    .UseCodeFirstDatabaseObjects()   // replaces the differ AND the migrations SQL generator
    .Options;
```

`UseCodeFirstDatabaseObjects()` installs `CodeFirstDatabaseObjectsModelDiffer` and
`CodeFirstDatabaseObjectsMigrationsSqlGenerator`. A context that declares procedures and nothing else may keep
the single `ReplaceService<IMigrationsModelDiffer, StoredProcedureModelDiffer>()` it has today — that type is
now a thin subclass of the new differ and behaves identically. A table placed on a scheme needs both
replacements, because only the generator writes the `ON` clause: the differ checks which generator is
registered and refuses to scaffold rather than let the table be created on the default filegroup in silence.
Both replacements must reach `dotnet ef` and the application alike, as with the differ alone.

`dotnet ef migrations add` then produces, in this order: the partition functions (`CREATE PARTITION FUNCTION`
behind `IF NOT EXISTS`), the schemes, columnstore drops — before EF's operations, because the direction that
removes an index may also re-create a clustered primary key, which SQL Server refuses while the columnstore
stands — then EF's own operations — where the placed table's `CreateTable` carries the scheme and column as
annotations, and its `CREATE CLUSTERED COLUMNSTORE INDEX` follows immediately, before any nonclustered
index, so those are built once over the columnstore rather than over a heap — then columnstore indexes added
to tables that already exist (each just before the first index the same migration adds to its table, when it
adds one), then procedures, then scheme drops and function drops. `Down` comes out of the same rules with
the sides swapped. Because the table is
already on its scheme when its indexes are created, the columnstore and every nonclustered index are aligned
to the scheme without any of them naming it, which is what makes a partition switchable or truncatable on
its own later.

Boundaries are T-SQL literals compared as text, so use one spelling: `PartitionBoundaries.Monthly` and
`PartitionBoundaries.Literal` produce invariant numbers and the two date spellings every SQL Server date
type reads as year-month-day under any session language — `'20260801'` for a date, ISO 8601 with `T` for a
datetime; legacy `datetime` reads dash-separated dates through the session's language, and a `dmy` login
would swap day and month — and the `params object[]`
overload of `HasPartitionFunction` renders values through `Literal` for you. Add a boundary and the next
migration carries an idempotent `SPLIT RANGE`, preceded by `ALTER PARTITION SCHEME … NEXT USED` for every
scheme on the function; remove one and it carries a `MERGE RANGE`. Keep the last partition empty — SQL Server
refuses to split a populated partition of a columnstore table and moves every row of a rowstore one — so add
months before they arrive.

What the differ refuses at `migrations add`, each with the fix in the message: a placement on a scheme that
is not declared; a scheme on a function that is not declared; a partition property that does not exist or
maps to no column of the table; a columnstore index on a table whose primary key, alternate key or index is
clustered (declare `HasNoKey()`, or `HasKey(…).IsClustered(false)`); a unique key or index on a partitioned
table that does not include the partitioning column, which SQL Server requires of every unique index aligned
to the scheme; and a placed table with any generator other than this package's. What it does not do, and
says so with a `NotSupportedException`: change a function's type or range direction, a scheme's function or
filegroup, or an existing table's scheme or column (renaming the table in the same migration does not hide
the change — tables are matched through their entity types, the way EF matches them when it scaffolds the
rename) — SQL Server has no `ALTER` for any of those, and moving a
table means rebuilding its clustered index over the data, which is a migration to write by hand with the
volume in view. A temporal or memory-optimized table cannot be placed on a scheme through this package.

---

## `[Sql]` — overriding the script

`[Sql]` is optional, and so is every facet inside it: what you state wins, what you leave out is read
from the procedure. Reach for it where the script cannot answer — the procedure is not part of this
compilation, the C# parameter name deliberately differs from the SQL one, or the header itself is not to
be trusted, as a bare `varchar` with no length is (SQL Server reads that as `varchar(1)`; **SPG012**
points at them). A name you do state must still start with `@` (**SPG004**). Some headers cannot answer
at all: a parameter the procedure types as `sql_variant`, `timestamp`, `sysname`, a spatial type, a
table-valued parameter or a `CURSOR` has no binding here, and the generator will not guess one from the
.NET type — **SPG016** asks you to state the type or change the procedure.

Where you leave a facet out and the script supplies it, **SPG017** notes the value the emitted contract
moved to. It is a warning and not a note, because the facet it reports is the one that turns a check
the validator used to skip into one it enforces: a stale script can move a contract that compiles today
into a startup failure. It stays silent when the fill changes nothing. State the facet in `[Sql]` if you
would rather pin the binding the declaration had before any script was read.

```csharp
[Sql("@AccountID", 21)]                      // name + length; the type still comes from the script
[Sql("@AccountID")]                          // name only — type and length come from the script
[Sql(Length = 21)]                           // length only — name and type come from the script
[Sql("@AccountID", "nvarchar", 21)]          // explicit type override; nothing left to infer
[Sql("@Price", Precision = 18, Scale = 2)]   // decimal(18,2)
[Sql("@Count", "int", Output = true)]        // OUTPUT direction + IsOutput in the contract
```

Where the attribute and the procedure both state a facet and disagree, the attribute wins and the
generator reports **SPG010**: one of the two is wrong, and a contract that disagrees with the deployed
procedure fails startup validation.

### .NET → SQL type inference

Consulted only when neither the attribute nor the procedure states a type.

| .NET | SQL | | .NET | SQL |
|------|-----|-|------|-----|
| `string` | `varchar` | | `bool` | `bit` |
| `int` | `int` | | `decimal` | `decimal` |
| `short` | `smallint` | | `float` | `real` |
| `byte` | `tinyint` | | `double` | `float` |
| `long` | `bigint` | | `DateTime` | `datetime` |
| `Guid` | `uniqueidentifier` | | `byte[]` | `varbinary` |

`string` defaults to `varchar` (not `nvarchar`); use `[Sql("@x", "nvarchar", n)]` when needed.

---

## Supported method shapes

| Return type | Behaviour |
|-------------|-----------|
| `Task<int>` | RETURN value |
| `Task<short>` | RETURN value as `short` |
| `Task<IReadOnlyList<TRow>>` | first result set as typed rows (`TRow` must be `[SqlRow]`) |
| `Task<(int, IReadOnlyList<TRow>)>` | RETURN value + result set |
| `Task<(int, T1, T2, ...)>` | RETURN value + OUTPUT parameter values (see below) |
| `Task` | execute, ignore RETURN/result |

`[SqlRow]` types must be **`partial` positional records**; columns map by member name
(override with `[SqlColumn("Name")]`).

### OUTPUT parameters

An OUTPUT parameter's value is surfaced through the return tuple: element `[0]` is the RETURN value,
the rest correspond to the OUTPUT parameters in declaration order. The direction is read from the
procedure header like any other facet — but only for a parameter carrying no `[Sql]` at all. Put any
`[Sql]` on it and the attribute becomes authoritative for the direction too, so `Output = true` has to
be spelled out there; that is what guarantees an existing declaration cannot change its OUTPUT arity
the day a script appears.

```csharp
[StoredProcedure("dbo.RESERVE_ID")]
public partial Task<(int ReturnValue, int NewId)> ReserveIdAsync(
    string prefix,                                     // @Prefix, read from the script
    [Sql("@NewId", "int", Output = true)] int newId,   // seed ignored for pure OUTPUT; result returned
    CancellationToken ct = default);

var (ret, newId) = await procs.ReserveIdAsync("AB", 0, ct);
```

The output parameter stays in the signature (its passed value seeds INPUT-OUTPUT procedures
and is ignored by SqlClient for pure OUTPUT). The tuple element count after the RETURN value
must equal the number of OUTPUT parameters in force (else **SPG009**). A method with no tuple at all is
the case SPG009 cannot see, and **SPG018** covers it: if the script says OUTPUT, the parameter carries no
`[Sql]` to say otherwise, and the return type has no element to receive the value, the procedure would
write it back into nothing.

## Row types from the EF model

A result row can be written out member by member, and always can be:

```csharp
[SqlRow]
public partial record ProductRow(
    [SqlColumn("Id")] int Id, string Sku, string Name, decimal Price, DateTime UpdatedUtc);
```

Or it can name the entity the procedure reads, and the generator takes the members from the EF model:

```csharp
[SqlRow(Entity = typeof(Product))]
public partial record ProductRow;
```

The members come from your `ModelSnapshot` - the file `dotnet ef migrations add` writes - read as
ordinary C# in the same compilation. Nothing runs, nothing connects, and the generator references no
EF assembly: it reads `Property<T>` for the CLR type, `IsRequired()` and the key list for nullability,
and `HasColumnName` for the column name. Because the snapshot is what EF itself reconciles every
configuration route into, it does not matter whether you configured the entity with data annotations,
with fluent `OnModelCreating`, with an `IEntityTypeConfiguration`, or by convention.

**The member order is the model's, not your class's** - EF orders the key first, then the rest
alphabetically - and the contract is positional, so your `SELECT` list has to match it. For `Product`
that is `Id, Name, Price, Sku, UpdatedUtc`. If you would rather fix the order at the call site, write
the record out by hand; both styles are supported and neither is legacy.

**Where it refuses.** The binding declines rather than guess, and every refusal is an error naming the
construct. The important one is a table more than one entity maps to: under TPH - EF's default
inheritance strategy - a derived type's non-nullable property is mapped to a *nullable* column, so a
row generated from the property would read `GetDecimal` on a column that can be NULL and throw on the
first sibling row. Inheritance, owned types, table splitting and value converters are all refused for
the same reason: the snapshot does not carry enough to be right, so it says so instead. Write the
record by hand for those.

A value converter whose provider type equals its CLR type - `decimal` to `decimal`, `string` to
`string` - leaves no trace in the snapshot and cannot be detected. That case binds the *stored* value
silently, and is the one blind spot in the refusal set.

## Diagnostics

| ID | Severity | Meaning |
|----|----------|---------|
| SPG001 | Error | group class is not `partial` |
| SPG002 | Error | unsupported method return type |
| SPG003 | Error | parameter has no SQL name: no `[Sql]`, and no matching parameter in the procedure |
| SPG004 | Error | SQL parameter name empty / not starting with `@` |
| SPG005 | Error | no SQL type from `[Sql]`, from the procedure, or from the .NET type |
| SPG006 | Error | `[SqlRow]` type is not a partial positional record |
| SPG007 | Error | result row type is not marked `[SqlRow]` |
| SPG008 | Error | row member type has no supported `IDataRecord` reader |
| SPG009 | Error | return tuple arity does not match the OUTPUT parameters |
| SPG010 | Warning | `[Sql]` and the procedure state one facet differently; the attribute wins |
| SPG011 | Warning | the procedure declares a parameter the method does not carry |
| SPG012 | Warning | a char/binary procedure parameter has no length, which SQL Server reads as `(1)` |
| SPG013 | Warning | the type the procedure declares cannot bind the parameter's .NET type |
| SPG014 | Warning | no `.sql` declaring this procedure reached the compiler |
| SPG015 | Warning | two `.sql` files declare the same procedure; nothing is inferred for it |
| SPG016 | Error | the procedure types the parameter with something unbindable (`sql_variant`, `timestamp`, `sysname`, a spatial type, a TVP, a `CURSOR`) and no `[Sql]` states a type |
| SPG017 | Warning | the procedure supplied a facet the `[Sql]` left unstated, and the emitted contract moved |
| SPG018 | Error | the procedure says OUTPUT, no `[Sql]` says otherwise, and the return type has nowhere to put the value |
| SPG019 | Warning | the procedure declares a construct the generator cannot map, so the .NET inference was kept |
| SPG020 | Warning | the procedure declares the parameter `OUTPUT` and the `[Sql]` states no direction |
| SPG021 | Error | `[SqlRow(Entity = ...)]` type is not a partial record |
| SPG022 | Error | `[SqlRow(Entity = ...)]` type also declares its own primary constructor |
| SPG023 | Error | the entity shares its table or view with another entity type |
| SPG024 | Error | the entity is part of an inheritance hierarchy |
| SPG025 | Error | the entity is owned by another entity type |
| SPG026 | Error | the entity is mapped through a construct this reader does not bind |
| SPG027 | Error | no EF Core model snapshot in this compilation |
| SPG028 | Error | `Entity` names a type that is not an entity type in any snapshot |
| SPG029 | Error | two snapshots describe the entity differently |
| SPG030 | Error | a model property has no `IDataRecord` reader |
| SPG031 | Error | column type and .NET type are not a pair `StoredProcedureValidator` accepts |
| SPG032 | Error | the model's store type and the class member's type disagree (a value converter) |
| SPG033 | Error | the entity contributed no members: every mapped property is a shadow property |

SPG001–SPG009 judge your C#; SPG010–SPG020 judge what the `.sql` says about it, and they are the reason
promoting scripts to a compile-time input is safe. The file is evidence, not authority: a disagreement
between it and the code is reported and then decided in favour of the code, so a stale or hand-edited
`.sql` cannot turn a compiling project into a wall of errors.

The script-derived diagnostics that *do* stop a build are held to a rule stricter than their severity:
each can fire only on a parameter that carries no `[Sql]` attribute at all — and such a parameter is an
SPG003 error already, before any script is read. A `.sql` is allowed to rescue a declaration the
generator used to reject; it is never allowed to reject one it used to accept. That is what makes
upgrading safe for a codebase that compiles today.

Silence them one at a time with `<NoWarn>`, or take the whole channel out with
`<R21SqlScriptDiscovery>false</R21SqlScriptDiscovery>`.
