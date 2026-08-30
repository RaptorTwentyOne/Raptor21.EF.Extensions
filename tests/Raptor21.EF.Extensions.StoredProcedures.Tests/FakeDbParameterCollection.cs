using System.Collections;
using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Raptor21.EF.Extensions.StoredProcedures.Tests;

/// <summary>
/// A minimal list-backed <see cref="DbParameterCollection"/>, so parameter post-processing can be driven
/// with no <see cref="SqlCommand"/> and no server.
/// </summary>
/// <remarks>
/// Only the collection is faked. The parameters it holds are real <see cref="SqlParameter"/> instances —
/// <see cref="SqlParameter"/> derives from <see cref="DbParameter"/> and is freely constructible — so the
/// values under test are the same type the executor really produces, and no parameter fake is needed.
/// </remarks>
internal sealed class FakeDbParameterCollection : DbParameterCollection
{
    private readonly List<DbParameter> _items;

    internal FakeDbParameterCollection(params DbParameter[] parameters) => _items = [.. parameters];

    /// <summary>
    /// The shape <c>BuildCommand</c> always produces: the RETURN slot at index 0, then the contract's
    /// parameters in declaration order.
    /// </summary>
    internal static FakeDbParameterCollection WithReturnSlot(params SqlParameter[] rest)
    {
        var all = new DbParameter[rest.Length + 1];
        all[0] = ReturnParameter();
        for (var i = 0; i < rest.Length; i++)
            all[i + 1] = rest[i];
        return new FakeDbParameterCollection(all);
    }

    /// <summary>
    /// A copy of the parameter <c>CreateReturnParameter</c> builds, optionally pre-populated the way the
    /// server would populate it — a test that wants a failure to be unambiguous can put a value here that
    /// must never appear among the collected outputs.
    /// </summary>
    internal static SqlParameter ReturnParameter(object? value = null) => new()
    {
        ParameterName = "@return",
        Direction = ParameterDirection.ReturnValue,
        SqlDbType = SqlDbType.Int,
        Value = value,
    };

    public override int Count => _items.Count;

    public override object SyncRoot => _items;

    protected override DbParameter GetParameter(int index) => _items[index];

    protected override DbParameter GetParameter(string parameterName) => _items[RequireIndexOf(parameterName)];

    protected override void SetParameter(int index, DbParameter value) => _items[index] = value;

    protected override void SetParameter(string parameterName, DbParameter value) =>
        _items[RequireIndexOf(parameterName)] = value;

    public override int Add(object value)
    {
        _items.Add((DbParameter)value);
        return _items.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var value in values)
            _items.Add((DbParameter)value!);
    }

    public override void Clear() => _items.Clear();

    public override bool Contains(object value) => _items.Contains((DbParameter)value);

    public override bool Contains(string value) => IndexOf(value) >= 0;

    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => _items.GetEnumerator();

    public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);

    // SqlParameterCollection matches names case-insensitively, and a test that looks a parameter up by
    // name should not have to know whether the code under test spelled it "@Id" or "@id".
    public override int IndexOf(string parameterName)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (string.Equals(_items[i].ParameterName, parameterName, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);

    public override void Remove(object value) => _items.Remove((DbParameter)value);

    public override void RemoveAt(int index) => _items.RemoveAt(index);

    public override void RemoveAt(string parameterName) => _items.RemoveAt(RequireIndexOf(parameterName));

    private int RequireIndexOf(string parameterName)
    {
        var index = IndexOf(parameterName);
        if (index < 0)
            throw new IndexOutOfRangeException(parameterName);
        return index;
    }
}
