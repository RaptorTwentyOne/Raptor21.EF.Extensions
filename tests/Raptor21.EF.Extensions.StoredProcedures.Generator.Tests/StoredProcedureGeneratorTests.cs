using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Generator.Tests;

public class StoredProcedureGeneratorTests
{
    private const string Usings = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Raptor21.EF.Extensions.StoredProcedures.Generated;
        """;

    [Fact]
    public void ScalarProcs_GenerateTypedMethodsAndRegistry()
    {
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class AccountProcedures
            {
                [StoredProcedure("dbo.ACCOUNT_LOGIN")]
                public partial Task<int> LoginAsync([Sql("@AccountID", 21)] string accountId, [Sql("@Password", 28)] string password, CancellationToken ct = default);

                [StoredProcedure("dbo.ACCOUNT_PREMIUM")]
                public partial Task<short> PremiumAsync([Sql("@AccountID", 21)] string accountId, CancellationToken ct = default);
            }
            """);

        Assert.True(result.OutputErrors.IsDefaultOrEmpty, result.OutputErrorSummary());
        Assert.Contains("ExecuteReturnInt32Async", result.GeneratedText);
        Assert.Contains("ExecuteReturnInt16Async", result.GeneratedText);
        Assert.Contains("ProcParamSpec(\"@AccountID\", new global::Raptor21.EF.Extensions.StoredProcedures.Contracts.SqlTypeSpec(\"varchar\", 21))", result.GeneratedText);
        Assert.Contains("class GeneratedProcedureRegistry", result.GeneratedText);
        Assert.Contains("__Contract_LoginAsync", result.GeneratedText);
        Assert.Contains("__Contract_PremiumAsync", result.GeneratedText);
    }

    [Fact]
    public void ResultSet_GeneratesFromDataRecordAndResultColumns()
    {
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [SqlRow]
            public partial record CharRow([SqlColumn("CharID")] int CharId, string Name, int? Level);

            [StoredProcedureGroup]
            public partial class CharProcedures
            {
                [StoredProcedure("dbo.GET_CHARS")]
                public partial Task<IReadOnlyList<CharRow>> GetCharsAsync([Sql("@AccountID", 21)] string accountId, CancellationToken ct = default);
            }
            """);

        Assert.True(result.OutputErrors.IsDefaultOrEmpty, result.OutputErrorSummary());
        Assert.Contains("static CharRow FromDataRecord", result.GeneratedText);
        Assert.Contains("ExecuteReturnResultSetAsync<global::Demo.CharRow>", result.GeneratedText);
        Assert.Contains("ColumnSpec(\"CharID\", typeof(int))", result.GeneratedText);
        Assert.Contains("ColumnSpec(\"Level\", typeof(int?))", result.GeneratedText);
        Assert.Contains("IsDBNull(record.GetOrdinal(\"Level\"))", result.GeneratedText);
    }

    [Fact]
    public void ResultSet_WithReturnValue_ReturnsTuple()
    {
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [SqlRow]
            public partial record Row(int Id);

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<(int ReturnValue, IReadOnlyList<Row> Rows)> XAsync([Sql("@Id", "int")] int id, CancellationToken ct = default);
            }
            """);

        Assert.True(result.OutputErrors.IsDefaultOrEmpty, result.OutputErrorSummary());
        Assert.Contains("return await __executor.ExecuteReturnResultSetAsync<global::Demo.Row>", result.GeneratedText);
    }

    [Fact]
    public void NonQueryProc_UsesExecuteNonQuery()
    {
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.DO_THING")]
                public partial Task DoThingAsync([Sql("@Id", "int")] int id, CancellationToken ct = default);
            }
            """);

        Assert.True(result.OutputErrors.IsDefaultOrEmpty, result.OutputErrorSummary());
        Assert.Contains("ExecuteNonQueryAsync", result.GeneratedText);
        Assert.DoesNotContain("return await __executor", result.GeneratedText);
    }

    [Fact]
    public void OutputParameter_FlowsToContractAsIsOutput()
    {
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task XAsync([Sql("@Count", "int", Output = true)] int count, CancellationToken ct = default);
            }
            """);

        Assert.True(result.OutputErrors.IsDefaultOrEmpty, result.OutputErrorSummary());
        Assert.Contains("ProcParamSpec(\"@Count\", new global::Raptor21.EF.Extensions.StoredProcedures.Contracts.SqlTypeSpec(\"int\"), true)", result.GeneratedText);
    }

    [Fact]
    public void DecimalParameter_EmitsPrecisionAndScale()
    {
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync([Sql("@Price", Precision = 18, Scale = 2)] decimal price, CancellationToken ct = default);
            }
            """);

        Assert.True(result.OutputErrors.IsDefaultOrEmpty, result.OutputErrorSummary());
        Assert.Contains("SqlTypeSpec(\"decimal\", null, (byte)18, (byte)2)", result.GeneratedText);
    }

    [Fact]
    public void OutputParameters_SurfacedThroughReturnTuple()
    {
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.RESERVE")]
                public partial Task<(int ReturnValue, int NewId, string? Code)> ReserveAsync(
                    [Sql("@Prefix", 10)] string prefix,
                    [Sql("@NewId", "int", Output = true)] int newId,
                    [Sql("@Code", 8, Output = true)] string code,
                    CancellationToken ct = default);
            }
            """);

        Assert.True(result.OutputErrors.IsDefaultOrEmpty, result.OutputErrorSummary());
        Assert.Contains("ExecuteWithOutputsAsync", result.GeneratedText);
        Assert.Contains("(int)__outs[0]!", result.GeneratedText);
        Assert.Contains("__outs[1] as string", result.GeneratedText);
        Assert.Contains("return (__ret,", result.GeneratedText);
        // both output params flow to the contract as IsOutput
        Assert.Contains("ProcParamSpec(\"@NewId\", new global::Raptor21.EF.Extensions.StoredProcedures.Contracts.SqlTypeSpec(\"int\"), true)", result.GeneratedText);
        Assert.Contains("ProcParamSpec(\"@Code\", new global::Raptor21.EF.Extensions.StoredProcedures.Contracts.SqlTypeSpec(\"varchar\", 8), true)", result.GeneratedText);
    }

    [Fact]
    public void OutputArityMismatch_ReportsSpg009()
    {
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<(int ReturnValue, int NewId)> XAsync([Sql("@Prefix", 10)] string prefix, CancellationToken ct = default);
            }
            """);

        Assert.True(result.HasGeneratorDiagnostic("SPG009"));
    }

    [Fact]
    public void MissingSqlAttribute_ReportsSpg003()
    {
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync(string accountId, CancellationToken ct = default);
            }
            """);

        Assert.True(result.HasGeneratorDiagnostic("SPG003"));
    }

    [Fact]
    public void NonPartialGroup_ReportsSpg001()
    {
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public class P
            {
                [StoredProcedure("dbo.X")]
                public Task<int> XAsync([Sql("@Id", "int")] int id, CancellationToken ct = default) => Task.FromResult(0);
            }
            """);

        Assert.True(result.HasGeneratorDiagnostic("SPG001"));
    }

    [Fact]
    public void UnsupportedReturn_ReportsSpg002()
    {
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<string> XAsync([Sql("@Id", "int")] int id, CancellationToken ct = default);
            }
            """);

        Assert.True(result.HasGeneratorDiagnostic("SPG002"));
    }

    [Fact]
    public void InvalidSqlName_ReportsSpg004()
    {
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync([Sql("Id", "int")] int id, CancellationToken ct = default);
            }
            """);

        Assert.True(result.HasGeneratorDiagnostic("SPG004"));
    }
}
