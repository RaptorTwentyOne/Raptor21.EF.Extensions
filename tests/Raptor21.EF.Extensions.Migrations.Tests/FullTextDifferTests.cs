using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Raptor21.EF.Extensions.Migrations.Tests;

/// <summary>
/// Drives the full-text half of <see cref="CodeFirstDatabaseObjectsModelDiffer"/> through real
/// <see cref="DbContext"/> models: where a catalog and an index land among EF's operations, that every
/// full-text statement is transaction-suppressed, the rebuild forced by a dropped key index, and every refusal
/// the differ makes at <c>migrations add</c>.
/// </summary>
public class FullTextDifferTests
{
    private const string ProcedureName = "dbo.FindCustomers";
    private const string Procedure = "CREATE OR ALTER PROCEDURE dbo.FindCustomers AS\nSELECT * FROM dbo.Customers WHERE CONTAINS(Name, 'x')\n";
    private const string EditedProcedure = "CREATE OR ALTER PROCEDURE dbo.FindCustomers AS\nSELECT * FROM dbo.Customers WHERE CONTAINS(Name, 'y')\n";

    private const string CreateIndex =
        "IF NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'[dbo].[Customers]'))\n" +
        "    CREATE FULLTEXT INDEX ON [dbo].[Customers] ([FullName], [Email]) KEY INDEX [PK_Customers] ON [ft_Account] WITH CHANGE_TRACKING AUTO;";

    [Fact]
    public void NewTableWithAFullTextIndex_CatalogFirst_IndexAfterEveryIndex_BeforeTheProcedure()
    {
        var operations = Diff<Baseline, CustomersFullText>();

        // The catalog before anything EF produced: it does not depend on a table, and the index does depend on it.
        Assert.Contains("CREATE FULLTEXT CATALOG [ft_Account]", Sql(operations[0]), StringComparison.Ordinal);

        // The index after the last index EF creates — the key index is among them — and before the procedure,
        // which binds CONTAINS against it when the table already exists.
        var create = Assert.Single(operations, o => Sql(o).Contains("CREATE FULLTEXT INDEX", StringComparison.Ordinal));
        Assert.Equal(CreateIndex, Sql(create));
        var createAt = operations.IndexOf(create);
        Assert.True(createAt > operations.FindLastIndex(o => o is CreateIndexOperation));
        Assert.True(createAt > operations.FindIndex(o => o is CreateTableOperation { Name: "Customers" }));
        Assert.True(createAt < operations.FindIndex(o => Sql(o).Contains("CREATE OR ALTER PROCEDURE", StringComparison.Ordinal)));

        // The property FullName maps to column FullName here; the EMAIL property maps to Email through
        // HasColumnName, and the statement names the column.
        Assert.Contains("([FullName], [Email])", Sql(create), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFullTextStatement_IsTransactionSuppressed()
    {
        // SQL Server refuses CREATE/DROP FULLTEXT CATALOG and INDEX inside a user transaction (error 574), and a
        // migration runs in one. SuppressTransaction is what makes EF commit before the statement and reopen after.
        var up = Diff<Baseline, CustomersFullText>();
        var down = Diff<CustomersFullText, Baseline>();

        var fullText = up.Concat(down).OfType<SqlOperation>().Where(o => o.Sql.Contains("FULLTEXT", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(fullText);
        Assert.All(fullText, o => Assert.True(o.SuppressTransaction));

        var others = up.Concat(down).OfType<SqlOperation>().Where(o => !o.Sql.Contains("FULLTEXT", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(others);
        Assert.All(others, o => Assert.False(o.SuppressTransaction));
    }

    [Fact]
    public void DroppedTable_TakesItsIndexWithIt_AndTheCatalogGoesLast()
    {
        var operations = Diff<CustomersFullText, Baseline>();

        Assert.Single(operations.OfType<DropTableOperation>(), o => o.Name == "Customers");
        Assert.DoesNotContain(operations, o => Sql(o).Contains("DROP FULLTEXT INDEX", StringComparison.Ordinal));
        Assert.Contains("DROP FULLTEXT CATALOG [ft_Account]", Sql(operations[^1]), StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingTableGainingAnIndex_CreatesItBeforeTheProcedure_AndDropsItBeforeEverythingElse()
    {
        var added = Diff<CustomersPlain, CustomersFullTextEditedProcedure>();
        Assert.Collection(
            added,
            op => Assert.Contains("CREATE FULLTEXT CATALOG", Sql(op), StringComparison.Ordinal),
            op => Assert.Equal(CreateIndex, Sql(op)),
            op => Assert.StartsWith("EXEC(N'CREATE OR ALTER PROCEDURE", Sql(op), StringComparison.Ordinal));

        var removed = Diff<CustomersFullTextEditedProcedure, CustomersPlain>();
        Assert.Collection(
            removed,
            op => Assert.Contains("DROP FULLTEXT INDEX ON [dbo].[Customers];", Sql(op), StringComparison.Ordinal),
            op => Assert.StartsWith("EXEC(N'CREATE OR ALTER PROCEDURE", Sql(op), StringComparison.Ordinal),
            op => Assert.Contains("DROP FULLTEXT CATALOG", Sql(op), StringComparison.Ordinal));
    }

    [Fact]
    public void ChangedIndex_IsDroppedAheadOfEfAndCreatedBehindIt()
    {
        var operations = Diff<CustomersFullText, CustomersFullTextTurkish>();

        Assert.Collection(
            operations,
            op => Assert.Contains("DROP FULLTEXT INDEX", Sql(op), StringComparison.Ordinal),
            op => Assert.Contains("([FullName] LANGUAGE 1055, [Email] LANGUAGE 1055)", Sql(op), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(typeof(Baseline), typeof(CustomersFullText), true)]
    [InlineData(typeof(CustomersFullText), typeof(Baseline), true)]
    [InlineData(typeof(CustomersFullText), typeof(CustomersFullTextTwin), false)]
    [InlineData(typeof(CustomersFullText), typeof(CustomersFullTextTurkish), true)]
    [InlineData(typeof(CustomersFullText), typeof(CustomersFullTextOnCode), true)]
    [InlineData(typeof(CustomersPlain), typeof(CustomersPlainWithCatalog), true)]
    public void HasDifferences_AnswersExactlyWhatGetDifferencesProduces(Type sourceType, Type targetType, bool expectDifference)
    {
        using var source = (DbContext)Activator.CreateInstance(sourceType)!;
        using var target = (DbContext)Activator.CreateInstance(targetType)!;

        var differ = Differ(source);
        var sourceModel = RelationalModel(source);
        var targetModel = RelationalModel(target);

        var operations = differ.GetDifferences(sourceModel, targetModel).Count;

        Assert.Equal(expectDifference, operations > 0);
        Assert.Equal(operations > 0, differ.HasDifferences(sourceModel, targetModel));
    }

    [Fact]
    public void RenamedTableKeepingItsIndex_EmitsNoFullTextOperationAtAll()
    {
        // Keyed on an explicitly named unique index, which the rename leaves alone: EF matches the table through
        // its entity type and scaffolds a RenameTableOperation, the differ makes the same match, and the index
        // that merely followed its table is not read as dropped and created.
        var operations = Diff<CustomersFullTextOnCode, CustomersFullTextOnCodeRenamedTable>();

        Assert.Contains(operations, o => o is RenameTableOperation);
        Assert.DoesNotContain(operations, o => Sql(o).Contains("FULLTEXT", StringComparison.Ordinal));
    }

    [Fact]
    public void RenamedTableKeyedOnItsPrimaryKey_IsDroppedUnderTheOldNameAndCreatedUnderTheNew()
    {
        // The primary key follows EF's naming convention, so the rename changes the key index from
        // PK_Customers to PK_Accounts and the index has to be rebuilt: the DROP before the rename, naming the
        // table that exists at that point, and the CREATE after it, naming the new table and the new key.
        var operations = Diff<CustomersFullText, CustomersFullTextRenamedTable>();

        var drop = Assert.Single(operations, o => Sql(o).Contains("DROP FULLTEXT INDEX", StringComparison.Ordinal));
        var create = Assert.Single(operations, o => Sql(o).Contains("CREATE FULLTEXT INDEX", StringComparison.Ordinal));
        var rename = operations.FindIndex(o => o is RenameTableOperation);

        Assert.Contains("DROP FULLTEXT INDEX ON [dbo].[Customers];", Sql(drop), StringComparison.Ordinal);
        Assert.Contains("CREATE FULLTEXT INDEX ON [dbo].[Accounts] (", Sql(create), StringComparison.Ordinal);
        Assert.Contains("KEY INDEX [PK_Accounts]", Sql(create), StringComparison.Ordinal);
        Assert.True(operations.IndexOf(drop) < rename);
        Assert.True(operations.IndexOf(create) > rename);
        Assert.True(operations.IndexOf(create) > operations.FindLastIndex(o => o is AddPrimaryKeyOperation));
    }

    [Fact]
    public void RenamedTableLosingItsIndex_DropsItUnderTheOldName_BeforeTheRename()
    {
        var operations = Diff<CustomersFullText, CustomersFullTextRenamedTableWithoutIndex>();

        var drop = Assert.Single(operations, o => Sql(o).Contains("DROP FULLTEXT INDEX", StringComparison.Ordinal));
        Assert.Contains("DROP FULLTEXT INDEX ON [dbo].[Customers];", Sql(drop), StringComparison.Ordinal);
        Assert.True(operations.IndexOf(drop) < operations.FindIndex(o => o is RenameTableOperation));
    }

    [Fact]
    public void KeyIndexDroppedByEf_ForcesTheIndexOutBeforeAndBackInAfter()
    {
        // IsClustered(false) on the primary key scaffolds a DropPrimaryKey at the front and an AddPrimaryKey at
        // the back. The full-text index is keyed on that constraint, and SQL Server refuses to drop it while
        // the full-text index stands (error 3723) — the declaration did not change, so the diff alone would
        // have said nothing.
        var up = Diff<CustomersFullText, CustomersFullTextNonclusteredKey>();

        var drop = Assert.Single(up, o => Sql(o).Contains("DROP FULLTEXT INDEX", StringComparison.Ordinal));
        var create = Assert.Single(up, o => Sql(o).Contains("CREATE FULLTEXT INDEX", StringComparison.Ordinal));
        Assert.True(up.IndexOf(drop) < up.FindIndex(o => o is DropPrimaryKeyOperation));
        Assert.True(up.IndexOf(create) > up.FindIndex(o => o is AddPrimaryKeyOperation));
        Assert.True(((SqlOperation)drop).SuppressTransaction);
        Assert.True(((SqlOperation)create).SuppressTransaction);

        // The declaration alone reports no difference; only the scaffold, which sees EF's operations, adds the rebuild.
        Assert.Equal(2, up.Count(o => Sql(o).Contains("FULLTEXT", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(typeof(UndeclaredCatalog), "HasFullTextCatalog(\"ft_missing\")")]
    [InlineData(typeof(CompositeKeyWithoutKeyIndex), "has 2 columns")]
    [InlineData(typeof(KeyIndexNotUnique), "not a primary key, unique constraint or unique index")]
    [InlineData(typeof(NullableKeyColumn), "is nullable")]
    [InlineData(typeof(NonTextColumn), "character and xml")]
    [InlineData(typeof(BinaryColumn), "TYPE COLUMN")]
    [InlineData(typeof(UnmappedProperty), "'Missing'")]
    [InlineData(typeof(KeylessWithoutKeyIndex), "has none")]
    [InlineData(typeof(CatalogDeclaredInTwoCasings), "case-insensitively")]
    public void AModelThatCannotDeploy_IsRefusedAtScaffoldTime(Type targetType, string expectedInMessage)
    {
        using var source = new Baseline();
        using var target = (DbContext)Activator.CreateInstance(targetType)!;

        var differ = Differ(target);
        var sourceModel = RelationalModel(source);
        var targetModel = RelationalModel(target);

        var ex = Assert.Throws<InvalidOperationException>(() => differ.GetDifferences(sourceModel, targetModel));
        Assert.Contains(expectedInMessage, ex.Message);
        Assert.Throws<InvalidOperationException>(() => differ.HasDifferences(sourceModel, targetModel));
    }

    [Fact]
    public void KeyedOnAUniqueIndex_NamesThatIndex()
    {
        var operations = Diff<Baseline, CustomersFullTextOnCode>();

        var create = Assert.Single(operations, o => Sql(o).Contains("CREATE FULLTEXT INDEX", StringComparison.Ordinal));
        Assert.Contains("KEY INDEX [UX_Customers_Code]", Sql(create), StringComparison.Ordinal);
    }

    [Fact]
    public void HasFullTextIndex_WritesPropertyNamesAndTakesBothExpressionShapes()
    {
        var builder = new ModelBuilder().Entity<Customer>();

        builder.HasFullTextIndex("ft", c => c.FullName);
        Assert.Equal("v1|ft||AUTO|FullName", builder.Metadata[CodeFirstAnnotations.FullTextIndex]);

        builder.HasFullTextIndex("ft", c => new { c.FullName, c.EMAIL }, "UX_Customers_Code", FullTextChangeTracking.Manual);
        Assert.Equal("v1|ft|UX_Customers_Code|MANUAL|FullName,EMAIL", builder.Metadata[CodeFirstAnnotations.FullTextIndex]);

        Assert.Throws<ArgumentException>(() => builder.HasFullTextIndex("ft", c => c.Id + 1));
        Assert.Throws<ArgumentException>(() => builder.HasFullTextIndex("ft", c => new { Upper = c.FullName.ToUpper() }));
    }

    [Fact]
    public void HasFullTextCatalog_WritesTheVersionUnderThePrefixedName()
    {
        var modelBuilder = new ModelBuilder();

        modelBuilder.HasFullTextCatalog(" ft_Account ");

        Assert.Equal("v1", modelBuilder.Model.FindAnnotation(CodeFirstAnnotations.FullTextCatalogPrefix + "ft_Account")?.Value);
    }

    private static List<MigrationOperation> Diff<TSource, TTarget>()
        where TSource : DbContext, new()
        where TTarget : DbContext, new()
    {
        using var source = new TSource();
        using var target = new TTarget();
        return Differ(target).GetDifferences(RelationalModel(source), RelationalModel(target)).ToList();
    }

    private static string Sql(MigrationOperation operation) => (operation as SqlOperation)?.Sql ?? string.Empty;

    private static IMigrationsModelDiffer Differ(DbContext context)
        => Assert.IsAssignableFrom<CodeFirstDatabaseObjectsModelDiffer>(context.GetService<IMigrationsModelDiffer>());

    private static IRelationalModel RelationalModel(DbContext context)
        => context.GetService<IDesignTimeModel>().Model.GetRelationalModel();

    // One subclass per variant: EF caches the built model per DbContext type.
    private abstract class Variant : DbContext
    {
        protected sealed override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseSqlServer("Server=host-that-must-never-be-contacted;Database=none")
                .UseCodeFirstDatabaseObjects();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasDefaultSchema("dbo");
            modelBuilder.Entity<Widget>();
        }

        // A single-column primary key (the default key index), a unique index to key on instead, a non-unique
        // index to be refused, and one property whose column name differs so the translation is visible.
        protected static EntityTypeBuilder<Customer> Customers(ModelBuilder modelBuilder)
        {
            var customers = modelBuilder.Entity<Customer>();
            customers.ToTable("Customers");
            customers.Property(c => c.FullName).HasColumnType("nvarchar(200)");
            customers.Property(c => c.EMAIL).HasColumnName("Email").HasColumnType("nvarchar(254)");
            customers.Property(c => c.Code).HasColumnType("varchar(20)");
            customers.HasIndex(c => c.Code).IsUnique().HasDatabaseName("UX_Customers_Code");
            customers.HasIndex(c => c.EMAIL).IsUnique().HasDatabaseName("UX_Customers_Email");
            customers.HasIndex(c => c.FullName).HasDatabaseName("IX_Customers_FullName");
            return customers;
        }

        protected static void Script(ModelBuilder modelBuilder, string sql = Procedure)
            => modelBuilder.Model.SetAnnotation(StoredProcedureScript.AnnotationPrefix + ProcedureName, sql);
    }

    private sealed class Baseline : Variant
    {
    }

    private sealed class CustomersPlain : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            Customers(modelBuilder);
            Script(modelBuilder);
        }
    }

    private sealed class CustomersPlainWithCatalog : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.HasFullTextCatalog("ft_Account");
            Customers(modelBuilder);
            Script(modelBuilder);
        }
    }

    private sealed class CustomersFullText : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.HasFullTextCatalog("ft_Account");
            Customers(modelBuilder).HasFullTextIndex("ft_Account", c => new { c.FullName, c.EMAIL });
            Script(modelBuilder);
        }
    }

    private sealed class CustomersFullTextTwin : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.HasFullTextCatalog("ft_Account");
            Customers(modelBuilder).HasFullTextIndex("ft_Account", c => new { c.EMAIL, c.FullName });
            Script(modelBuilder);
        }
    }

    private sealed class CustomersFullTextEditedProcedure : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.HasFullTextCatalog("ft_Account");
            Customers(modelBuilder).HasFullTextIndex("ft_Account", c => new { c.FullName, c.EMAIL });
            Script(modelBuilder, EditedProcedure);
        }
    }

    private sealed class CustomersFullTextTurkish : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.HasFullTextCatalog("ft_Account");
            Customers(modelBuilder).HasFullTextIndex(
                "ft_Account", [new FullTextColumn(nameof(Customer.FullName), 1055), new FullTextColumn(nameof(Customer.EMAIL), 1055)]);
            Script(modelBuilder);
        }
    }

    private sealed class CustomersFullTextOnCode : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.HasFullTextCatalog("ft_Account");
            Customers(modelBuilder).HasFullTextIndex("ft_Account", c => new { c.FullName, c.EMAIL }, keyIndex: "UX_Customers_Code");
            Script(modelBuilder);
        }
    }

    private sealed class CustomersFullTextOnCodeRenamedTable : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.HasFullTextCatalog("ft_Account");
            Customers(modelBuilder).ToTable("Accounts").HasFullTextIndex("ft_Account", c => new { c.FullName, c.EMAIL }, keyIndex: "UX_Customers_Code");
            Script(modelBuilder);
        }
    }

    private sealed class CustomersFullTextRenamedTable : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.HasFullTextCatalog("ft_Account");
            Customers(modelBuilder).ToTable("Accounts").HasFullTextIndex("ft_Account", c => new { c.FullName, c.EMAIL });
            Script(modelBuilder);
        }
    }

    private sealed class CustomersFullTextRenamedTableWithoutIndex : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.HasFullTextCatalog("ft_Account");
            Customers(modelBuilder).ToTable("Accounts");
            Script(modelBuilder);
        }
    }

    private sealed class CustomersFullTextNonclusteredKey : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.HasFullTextCatalog("ft_Account");
            var customers = Customers(modelBuilder).HasFullTextIndex("ft_Account", c => new { c.FullName, c.EMAIL });
            customers.HasKey(c => c.Id).IsClustered(false);
            Script(modelBuilder);
        }
    }

    private sealed class UndeclaredCatalog : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            Customers(modelBuilder).HasFullTextIndex("ft_missing", c => c.FullName);
        }
    }

    private sealed class CompositeKeyWithoutKeyIndex : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.HasFullTextCatalog("ft_Account");
            var customers = Customers(modelBuilder).HasFullTextIndex("ft_Account", c => c.FullName);
            customers.HasKey(c => new { c.Id, c.Code });
        }
    }

    private sealed class KeyIndexNotUnique : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.HasFullTextCatalog("ft_Account");
            Customers(modelBuilder).HasFullTextIndex("ft_Account", c => c.FullName, keyIndex: "IX_Customers_FullName");
        }
    }

    private sealed class NullableKeyColumn : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.HasFullTextCatalog("ft_Account");
            Customers(modelBuilder).HasFullTextIndex("ft_Account", c => c.FullName, keyIndex: "UX_Customers_Email");
        }
    }

    private sealed class NonTextColumn : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.HasFullTextCatalog("ft_Account");
            Customers(modelBuilder).HasFullTextIndex("ft_Account", c => new { c.FullName, c.Age });
        }
    }

    private sealed class BinaryColumn : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.HasFullTextCatalog("ft_Account");
            Customers(modelBuilder).HasFullTextIndex("ft_Account", c => c.Document);
        }
    }

    private sealed class UnmappedProperty : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.HasFullTextCatalog("ft_Account");
            Customers(modelBuilder).HasFullTextIndex("ft_Account", [new FullTextColumn("Missing")]);
        }
    }

    private sealed class KeylessWithoutKeyIndex : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.HasFullTextCatalog("ft_Account");
            Customers(modelBuilder).HasNoKey().HasFullTextIndex("ft_Account", c => c.FullName);
        }
    }

    private sealed class CatalogDeclaredInTwoCasings : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.HasFullTextCatalog("ft_Account").HasFullTextCatalog("FT_ACCOUNT");
            Customers(modelBuilder).HasFullTextIndex("ft_Account", c => c.FullName);
        }
    }

    private sealed class Widget
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class Customer
    {
        public long Id { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string? EMAIL { get; set; }
        public string Code { get; set; } = string.Empty;
        public int Age { get; set; }
        public byte[] Document { get; set; } = [];
    }
}
