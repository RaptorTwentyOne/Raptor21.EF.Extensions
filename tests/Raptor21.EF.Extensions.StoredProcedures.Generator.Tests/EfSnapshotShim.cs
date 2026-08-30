namespace Raptor21.EF.Extensions.StoredProcedures.Generator.Tests;

/// <summary>
/// A faithful double of the EF Core vocabulary a <c>ModelSnapshot</c> is written in, compiled into the
/// test compilation as ordinary source.
/// </summary>
/// <remarks>
/// <para>
/// Not a cheat, and not a mock. <see cref="EfSnapshotReader"/> matches on fully-qualified <b>names</b>
/// and never on identity with a referenced assembly: the snapshot filter compares the base chain against
/// the string <c>global::Microsoft.EntityFrameworkCore.Infrastructure.ModelSnapshot</c>, the pipeline
/// asks Roslyn for the metadata name
/// <c>Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute</c>, and everything inside
/// <c>BuildModel</c> is read as syntax. Declaring those names here is therefore the same input a real EF
/// reference supplies - and it is what lets the reader's version-tolerance be tested against vocabulary
/// no shipped EF emits, which a package reference could never do.
/// </para>
/// <para>
/// The generator project has one <c>PackageReference</c>, to <c>Microsoft.CodeAnalysis.CSharp</c>, and
/// keeping it that way is the constraint this whole branch is built around. A test project that pulled
/// EF in would be testing a different program from the one that ships.
/// </para>
/// <para>
/// Every composed source is <c>#nullable disable</c>d, exactly as a generated snapshot and a scaffolded
/// entity class are. That is also why the reader treats <c>IsRequired()</c> rather than a <c>?</c> as
/// the authoritative nullability signal for a reference type: in this file, as in the real one, the
/// annotation means nothing.
/// </para>
/// </remarks>
internal static class EfSnapshotShim
{
    /// <summary>The namespace every probe entity type, snapshot and DbContext double is declared in.</summary>
    public const string ModelNamespace = "Probe";

    /// <summary>The namespace every row type and procedure group in these tests is declared in.</summary>
    public const string RowNamespace = "Demo";

    /// <summary>The EF surface a snapshot touches, declared under EF's own names.</summary>
    private const string Vocabulary = """
        namespace Microsoft.EntityFrameworkCore.Infrastructure
        {
            /// <summary>The base type the reader's semantic filter looks for, and the only one it looks for.</summary>
            public abstract class ModelSnapshot
            {
                protected virtual void BuildModel(ModelBuilder modelBuilder) { }
            }

            /// <summary>Carried by a snapshot AND by every per-migration Designer partial, which is why it cannot be the whole filter.</summary>
            [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
            public sealed class DbContextAttribute : Attribute
            {
                public DbContextAttribute(Type contextType) { ContextType = contextType; }

                public Type ContextType { get; }
            }
        }

        namespace Microsoft.EntityFrameworkCore.Migrations
        {
            /// <summary>What a Designer partial derives from - Migration, never ModelSnapshot.</summary>
            public class Migration
            {
                protected virtual void BuildTargetModel(ModelBuilder modelBuilder) { }
            }

            [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
            public sealed class MigrationAttribute : Attribute
            {
                public MigrationAttribute(string id) { Id = id; }

                public string Id { get; }
            }
        }

        namespace Microsoft.EntityFrameworkCore
        {
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            public class ModelBuilder
            {
                public ModelBuilder HasAnnotation(string name, object value) => this;

                public ModelBuilder HasDefaultSchema(string schema) => this;

                public EntityTypeBuilder Entity(string name, Action<EntityTypeBuilder> buildAction) => new EntityTypeBuilder();

                public EntityTypeBuilder SharedTypeEntity(string name, string clrType, Action<EntityTypeBuilder> buildAction) => new EntityTypeBuilder();
            }

            /// <summary>The provider extensions a snapshot calls as statics - the shape the root-expression rule exists for.</summary>
            public static class SqlServerPropertyBuilderExtensions
            {
                public static PropertyBuilder<T> UseIdentityColumn<T>(PropertyBuilder<T> builder, long seed = 1L, int increment = 1) => builder;

                public static PropertyBuilder<T> IsSparse<T>(PropertyBuilder<T> builder, bool sparse = true) => builder;
            }

            public static class SqlServerModelBuilderExtensions
            {
                public static ModelBuilder UseIdentityColumns(ModelBuilder builder, long seed = 1L, int increment = 1) => builder;
            }
        }

        namespace Microsoft.EntityFrameworkCore.Metadata.Builders
        {
            public class EntityTypeBuilder
            {
                public PropertyBuilder<T> Property<T>(string name) => new PropertyBuilder<T>();

                public PropertyBuilder<T> PrimitiveCollection<T>(string name) => new PropertyBuilder<T>();

                public KeyBuilder HasKey(params string[] propertyNames) => new KeyBuilder();

                public EntityTypeBuilder HasBaseType(string name) => this;

                public EntityTypeBuilder ToTable(string name) => this;

                public EntityTypeBuilder ToTable(string name, string schema) => this;

                public EntityTypeBuilder ToTable(string name, Action<TableBuilder> buildAction) => this;

                public EntityTypeBuilder ToTable(string name, string schema, Action<TableBuilder> buildAction) => this;

                public EntityTypeBuilder ToView(string name) => this;

                public EntityTypeBuilder ToView(string name, string schema) => this;

                public EntityTypeBuilder ToSqlQuery(string sql) => this;

                public EntityTypeBuilder ToFunction(string name) => this;

                public EntityTypeBuilder SplitToTable(string name, Action<TableBuilder> buildAction) => this;

                public EntityTypeBuilder SplitToTable(string name, string schema, Action<TableBuilder> buildAction) => this;

                public EntityTypeBuilder SplitToView(string name, Action<TableBuilder> buildAction) => this;

                public EntityTypeBuilder ComplexProperty<T>(string name, string complexTypeName, Action<EntityTypeBuilder> buildAction) => this;

                public EntityTypeBuilder ComplexCollection<T>(string name, string complexTypeName, Action<EntityTypeBuilder> buildAction) => this;

                public EntityTypeBuilder OwnsOne(string ownedTypeName, string navigationName, Action<EntityTypeBuilder> buildAction) => this;

                public EntityTypeBuilder OwnsMany(string ownedTypeName, string navigationName, Action<EntityTypeBuilder> buildAction) => this;

                public EntityTypeBuilder WithOwner() => this;

                public EntityTypeBuilder HasForeignKey(params string[] propertyNames) => this;

                public EntityTypeBuilder Navigation(string name) => this;

                public IndexBuilder HasIndex(params string[] propertyNames) => new IndexBuilder();

                public DiscriminatorBuilder HasDiscriminator() => new DiscriminatorBuilder();

                public DiscriminatorBuilder HasDiscriminator<T>(string name) => new DiscriminatorBuilder();

                public EntityTypeBuilder UseTphMappingStrategy() => this;

                public EntityTypeBuilder UseTptMappingStrategy() => this;

                public EntityTypeBuilder UseTpcMappingStrategy() => this;

                public EntityTypeBuilder HasAnnotation(string name, object value) => this;

                public EntityTypeBuilder IsRequired() => this;

                /// <summary>Vocabulary no shipped EF emits, for the version-tolerance test.</summary>
                public EntityTypeBuilder FutureEfCall(string argument) => this;
            }

            public class PropertyBuilder<T>
            {
                public PropertyBuilder<T> IsRequired() => this;

                public PropertyBuilder<T> IsRequired(bool required) => this;

                public PropertyBuilder<T> HasColumnName(string name) => this;

                public PropertyBuilder<T> HasColumnType(string type) => this;

                public PropertyBuilder<T> HasMaxLength(int length) => this;

                public PropertyBuilder<T> IsUnicode(bool unicode = true) => this;

                public PropertyBuilder<T> IsFixedLength(bool fixedLength = true) => this;

                public PropertyBuilder<T> IsConcurrencyToken(bool token = true) => this;

                public PropertyBuilder<T> ValueGeneratedOnAdd() => this;

                public PropertyBuilder<T> ValueGeneratedOnAddOrUpdate() => this;

                public PropertyBuilder<T> ValueGeneratedNever() => this;

                public PropertyBuilder<T> HasDefaultValueSql(string sql) => this;

                public PropertyBuilder<T> HasAnnotation(string name, object value) => this;
            }

            public class TableBuilder
            {
                public ColumnBuilder Property(string name) => new ColumnBuilder();

                public TableBuilder ExcludeFromMigrations() => this;
            }

            public class ColumnBuilder
            {
                public ColumnBuilder HasColumnName(string name) => this;
            }

            public class KeyBuilder
            {
                public KeyBuilder HasName(string name) => this;
            }

            public class IndexBuilder
            {
                public IndexBuilder IsUnique() => this;

                public IndexBuilder HasDatabaseName(string name) => this;
            }

            public class DiscriminatorBuilder
            {
                public DiscriminatorBuilder HasValue(object value) => this;
            }
        }

        namespace Probe
        {
            /// <summary>The DbContext a snapshot names. Carried for diagnostic text only; the entity join never uses it.</summary>
            public class ProbeContext { }

            /// <summary>A second context, for the two-snapshots case.</summary>
            public class ProbeContextTwo { }

            /// <summary>A const a hand-edited snapshot might name where EF would have written a literal.</summary>
            public static class TableNames
            {
                public const string Legacy = "Legacy";
            }
        }
        """;

    /// <summary>Everything a composed test source needs before its own declarations.</summary>
    /// <remarks>
    /// <c>#nullable disable</c> first, exactly as the generated snapshot carries it, and exactly as the
    /// generated row file the generator writes carries it.
    /// </remarks>
    private const string Header = """
        #nullable disable
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.EntityFrameworkCore;
        using Microsoft.EntityFrameworkCore.Infrastructure;
        using Microsoft.EntityFrameworkCore.Metadata.Builders;
        using Microsoft.EntityFrameworkCore.Migrations;
        using Raptor21.EF.Extensions.StoredProcedures.Generated;
        """;

    /// <summary>Builds one compilation unit: the header, the EF vocabulary, then the caller's declarations.</summary>
    public static string Compose(params string[] parts) =>
        string.Join("\n\n", new[] { Header, Vocabulary }.Concat(parts));

    /// <summary>The caller's declarations with the header and the vocabulary left OUT.</summary>
    /// <remarks>
    /// For a second syntax tree in the same compilation. Only one tree may declare the vocabulary, and
    /// only a tree the test does not replace should - the incrementality tests replace the snapshot's
    /// tree wholesale, and dragging the vocabulary along would re-declare every EF type on every edit.
    /// </remarks>
    public static string ComposeAdditional(params string[] parts) =>
        string.Join("\n\n", new[] { "#nullable disable", "using System;", "using Microsoft.EntityFrameworkCore;", "using Microsoft.EntityFrameworkCore.Infrastructure;", "using Raptor21.EF.Extensions.StoredProcedures.Generated;" }.Concat(parts));

    /// <summary>Wraps <c>BuildModel</c> statements in the class shape <c>dotnet ef migrations add</c> writes.</summary>
    /// <param name="className">The snapshot class name; it is what SPG029 names.</param>
    /// <param name="buildModelBody">The statements, verbatim.</param>
    /// <param name="contextTypeName">The <c>[DbContext(typeof(X))]</c> argument.</param>
    public static string Snapshot(string className, string buildModelBody, string contextTypeName = "ProbeContext") => $$"""
        namespace {{ModelNamespace}}
        {
            [DbContext(typeof({{contextTypeName}}))]
            partial class {{className}} : ModelSnapshot
            {
                protected override void BuildModel(ModelBuilder modelBuilder)
                {
        {{buildModelBody}}
                }
            }
        }
        """;

    /// <summary>The per-migration Designer partial: the same attribute, a different base and a different method.</summary>
    /// <remarks>
    /// Written as two partial declarations because that is how EF writes it - the attribute is on the
    /// Designer half and the base type on the other - which is also the shape that proves the reader
    /// resolves the base chain from the SYMBOL rather than from the attributed declaration's own syntax.
    /// </remarks>
    public static string DesignerMigration(string className, string migrationId, string buildTargetModelBody) => $$"""
        namespace {{ModelNamespace}}
        {
            public partial class {{className}} : Migration
            {
            }

            [DbContext(typeof(ProbeContext))]
            [Migration("{{migrationId}}")]
            partial class {{className}}
            {
                protected override void BuildTargetModel(ModelBuilder modelBuilder)
                {
        {{buildTargetModelBody}}
                }
            }
        }
        """;

    /// <summary>One <c>modelBuilder.Entity("name", b =&gt; { ... })</c> block.</summary>
    public static string Block(string entityName, string body) => $$"""
                    modelBuilder.Entity("{{entityName}}", b =>
                        {
        {{body}}
                        });
        """;

    /// <summary>The probe entity classes, wrapped in the model namespace.</summary>
    public static string Types(string declarations) => $$"""
        namespace {{ModelNamespace}}
        {
        {{declarations}}
        }
        """;

    /// <summary>The row types and procedure groups, wrapped in the consumer namespace.</summary>
    public static string Rows(string declarations) => $$"""
        namespace {{RowNamespace}}
        {
        {{declarations}}
        }
        """;

    /// <summary>A <c>[SqlRow(Entity = typeof(X))] public partial record R;</c> declaration.</summary>
    public static string BoundRow(string rowTypeName, string entityTypeName) =>
        $"    [SqlRow(Entity = typeof({ModelNamespace}.{entityTypeName}))]\n    public partial record {rowTypeName};";
}
