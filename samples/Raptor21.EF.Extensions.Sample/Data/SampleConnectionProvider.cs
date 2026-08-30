using Microsoft.Data.SqlClient;
using Raptor21.EF.Extensions.StoredProcedures.Execution;

namespace Raptor21.EF.Extensions.Sample.Data;

/// <summary>
/// Hands fresh connections to the generated procedure groups. In a real app this is a singleton
/// registered as <see cref="ISqlConnectionProvider"/>, usually wrapping the same connection string
/// the DbContext uses.
/// </summary>
public sealed class SampleConnectionProvider(string connectionString) : ISqlConnectionProvider
{
    public SqlConnection Create() => new(connectionString);
}
