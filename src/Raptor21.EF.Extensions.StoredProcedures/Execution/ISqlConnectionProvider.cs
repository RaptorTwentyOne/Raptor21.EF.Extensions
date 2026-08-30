using Microsoft.Data.SqlClient;

namespace Raptor21.EF.Extensions.StoredProcedures.Execution;

/// <summary>
/// Supplies fresh <see cref="SqlConnection"/> instances to generated stored-procedure
/// group methods. The generated method body owns the connection lifetime (creates, opens
/// and disposes it), so the caller never repeats CreateConnection/OpenAsync boilerplate.
/// </summary>
public interface ISqlConnectionProvider
{
    /// <summary>Creates a new, unopened <see cref="SqlConnection"/>.</summary>
    SqlConnection Create();
}
