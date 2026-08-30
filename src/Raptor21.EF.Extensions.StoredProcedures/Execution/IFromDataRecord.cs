using System.Data;

namespace Raptor21.EF.Extensions.StoredProcedures.Execution;

public interface IFromDataRecord<TSelf> where TSelf : IFromDataRecord<TSelf>
{
    static abstract TSelf FromDataRecord(IDataRecord record);
}
