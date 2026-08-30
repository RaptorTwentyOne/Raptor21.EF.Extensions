using Microsoft.EntityFrameworkCore;
using Raptor21.EF.Extensions.Sample.Catalog;
using Raptor21.EF.Extensions.Sample.Data;
using Raptor21.EF.Extensions.Sample.Generated;
using Raptor21.EF.Extensions.StoredProcedures;
using Raptor21.EF.Extensions.StoredProcedures.Execution;

var connectionString = CatalogDbContextFactory.ConnectionString;
Console.WriteLine($"Database: {connectionString}");

// 1. EF migrations bring the schema up to date - and because the procedure scripts are registered on
//    the model, the same migration carries the CREATE OR ALTER for every procedure. Tables and
//    procedures move together or not at all.
await using (var db = new CatalogDbContext(CatalogDbContextFactory.BuildOptions(connectionString)))
{
    await db.Database.MigrateAsync();
    Console.WriteLine("Migrations applied (tables + procedures).");
}

// 2. Belt and braces for apps that do not migrate at boot: apply the scripts idempotently, then check
//    every generated contract against the live database. A procedure someone edited by hand, a renamed
//    parameter, a changed length - all of it fails here rather than on the first call in production.
await StoredProcedureSchemaManager.ApplyAndValidateAsync(
    connectionString,
    typeof(Program).Assembly,
    CatalogDbContext.ScriptResourcePrefix + ".",
    GeneratedProcedureRegistry.All,
    useTransaction: true);
Console.WriteLine($"Validated {GeneratedProcedureRegistry.All.Count} contracts against the live schema.");

// 3. Call the procedures. Nothing below uses reflection, so this sample publishes AOT.
var procedures = new CatalogProcedures(
    new SampleConnectionProvider(connectionString),
    new StoredProcedureExecutor());

var (inserted, id) = await procedures.UpsertAsync("KO-1000", "Elixir of Life", 249.90m, 0);
Console.WriteLine($"Upsert -> {(inserted == 1 ? "inserted" : "updated")} id={id}");

await procedures.TouchStampAsync(id);

var bySku = await procedures.GetBySkuAsync("KO-1000");
foreach (var row in bySku)
    Console.WriteLine($"  {row.Id,4}  {row.Sku,-10} {row.Name,-20} {row.Price,10:N2}  {row.UpdatedUtc:u}");

var (total, page) = await procedures.ListAsync(10);
Console.WriteLine($"Listed {page.Count} of {total} products.");

// The same kind of call, with the row type's members generated from the EF model rather than written
// out by hand. The two row types this sample calls through are the two answers to one question - who
// says what the shape is. ProductRow: a C# file does, and always may. ProductListRow: the model does,
// and the developer says which entity.
//
// ProductListRow is `[SqlRow(Entity = typeof(ProductListResult))] partial record` and
// nothing else. Its four members, their types, their column names and their nullability came out of
// Migrations/CatalogDbContextModelSnapshot.cs at compile time - no reflection here either, and no
// database was consulted to produce them. vw_ProductList is never created and never queried; the
// keyless entity exists to say "this is a result shape, not a table".
var listed = await procedures.ListViewAsync(10);
foreach (var row in listed)
    Console.WriteLine($"  {row.Id,4}  {row.Sku,-10} {row.Name,-20} {row.Price,10:N2}");
