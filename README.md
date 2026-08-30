# Raptor21.EF.Extensions

Code-first SQL Server database objects for EF Core. Procedures live in your repository, are called
through a compile-time contract, and travel in the same migration as the tables they touch — with no
runtime reflection, so a consumer can still publish AOT.

Entity Framework gives you code-first tables. It gives you nothing for the rest of the database, which
is how procedure bodies end up living only in production, versioned by whoever last opened SSMS. That is
the gap this family closes.

| Package | What it does |
|---|---|
| **`Raptor21.EF.Extensions.StoredProcedures`** | The runtime — contracts, a contract-driven executor, a live-schema validator and an idempotent script applier — plus the Roslyn source generator, which ships inside this package. |
| **`Raptor21.EF.Extensions.Migrations`** | Registers those scripts on the EF model and diffs them, so `dotnet ef migrations add` emits `CREATE OR ALTER` / `DROP` alongside the table DDL. EF Core 10. |

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

The generator arrives with the first package; there is nothing else to wire up.

Declare a group. Every `[StoredProcedure]` partial method is all you write:

```csharp
using Raptor21.EF.Extensions.StoredProcedures.Generated;

[StoredProcedureGroup]
public partial class AccountProcedures
{
    // RETURN value (SQL Server RETURN is always int; short is also supported)
    [StoredProcedure("dbo.ACCOUNT_LOGIN")]
    public partial Task<int> LoginAsync(
        [Sql("@AccountID", 21)] string accountId,   // varchar(21), inferred from string
        [Sql("@Password", 28)]  string password,
        CancellationToken ct = default);

    // Result set -> typed rows
    [StoredProcedure("dbo.GET_CHARACTER_LIST")]
    public partial Task<IReadOnlyList<CharRow>> GetCharactersAsync(
        [Sql("@AccountID", 21)] string accountId,
        CancellationToken ct = default);

    // Result set + RETURN value
    [StoredProcedure("dbo.GET_CHARACTER_LIST2")]
    public partial Task<(int ReturnValue, IReadOnlyList<CharRow> Rows)> GetCharactersWithReturnAsync(
        [Sql("@AccountID", 21)] string accountId,
        CancellationToken ct = default);

    // No RETURN / result set consumed
    [StoredProcedure("dbo.TOUCH_LAST_LOGIN")]
    public partial Task TouchLastLoginAsync(
        [Sql("@AccountID", 21)] string accountId,
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

### Dependency injection

```csharp
services.AddSingleton<DbConnectionFactory>();                 // implements ISqlConnectionProvider
services.AddSingleton<ISqlConnectionProvider>(sp => sp.GetRequiredService<DbConnectionFactory>());
services.AddSingleton<IStoredProcedureExecutor, StoredProcedureExecutor>();
services.AddSingleton<AccountProcedures>();
```

### Startup: apply scripts + validate contracts against the live DB

```csharp
await StoredProcedureSchemaManager.ApplyAndValidateAsync(
    connectionString,
    typeof(Program).Assembly,
    "MyApp.DbScripts.",                                   // embedded .sql resource prefix
    MyApp.Generated.GeneratedProcedureRegistry.All,       // generated; no manual list
    useTransaction: true,
    ct);
```

`ValidateAsync` compares each contract to the real procedure (`sys.parameters`,
`sys.dm_exec_describe_first_result_set_for_object`) and throws on the first mismatch in
parameter names/types/lengths or result-set columns. Fail fast at boot.

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

`dotnet ef migrations add Initial` then produces one migration containing the table DDL **and** a
`CREATE OR ALTER` per procedure, with `DROP PROCEDURE IF EXISTS` in `Down`. Change a procedure body and
the next migration carries only that change; the annotations round-trip through the model snapshot, so
the diff is against what was last migrated, not against the database.

Each script must be a single `CREATE OR ALTER PROCEDURE` batch — no `GO` separators, and `CREATE OR
ALTER` rather than plain `CREATE`, because idempotency is what makes re-running a migration safe.

Two limits worth knowing before you rely on this: `dotnet ef migrations has-pending-model-changes` does
not yet notice procedure-only edits, and `migrations script --idempotent` wraps the operation in
`IF ... BEGIN`, which SQL Server rejects for `CREATE OR ALTER`. Both are listed in
[CHANGELOG.md](CHANGELOG.md) under known gaps.

---

## `[Sql]` attribute

`[Sql]`'s first argument is the **mandatory** SQL parameter name (must start with `@`).
The SQL type is inferred from the .NET type; length/precision/scale or an explicit type
name can be supplied.

```csharp
[Sql("@AccountID", 21)]                 // varchar(21) inferred from string
[Sql("@AccountID")]                     // length not validated
[Sql("@AccountID", "nvarchar", 21)]     // explicit type override
[Sql("@Price", Precision = 18, Scale = 2)]   // decimal(18,2)
[Sql("@Count", "int", Output = true)]   // OUTPUT direction + IsOutput in the contract
```

### .NET → SQL type inference

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

Mark a parameter `[Sql("@x", ..., Output = true)]` and surface its value through the return
tuple: element `[0]` is the RETURN value, the rest correspond to the `Output = true`
parameters in declaration order.

```csharp
[StoredProcedure("dbo.RESERVE_ID")]
public partial Task<(int ReturnValue, int NewId)> ReserveIdAsync(
    [Sql("@Prefix", 10)] string prefix,
    [Sql("@NewId", "int", Output = true)] int newId,   // seed ignored for pure OUTPUT; result returned
    CancellationToken ct = default);

var (ret, newId) = await procs.ReserveIdAsync("AB", 0, ct);
```

The output parameter stays in the signature (its passed value seeds INPUT-OUTPUT procedures
and is ignored by SqlClient for pure OUTPUT). The tuple element count after the RETURN value
must equal the number of `Output = true` parameters (else **SPG009**).

## Diagnostics

| ID | Meaning |
|----|---------|
| SPG001 | group class is not `partial` |
| SPG002 | unsupported method return type |
| SPG003 | parameter missing `[Sql]` |
| SPG004 | SQL parameter name empty / not starting with `@` |
| SPG005 | parameter .NET type has no inferable SQL type |
| SPG006 | `[SqlRow]` type is not a partial positional record |
| SPG007 | result row type is not marked `[SqlRow]` |
| SPG008 | row member type has no supported `IDataRecord` reader |
| SPG009 | return tuple arity does not match the `Output = true` parameters |
