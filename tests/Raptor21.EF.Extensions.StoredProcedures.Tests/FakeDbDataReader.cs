using System.Collections;
using System.Data.Common;
using System.Runtime.CompilerServices;

namespace Raptor21.EF.Extensions.StoredProcedures.Tests;

/// <summary>
/// In-memory <see cref="DbDataReader"/> over canned columns and rows. Because <see cref="DbDataReader"/>
/// implements <see cref="System.Data.IDataRecord"/>, one instance serves both the generated row
/// materialisers (which take an <see cref="System.Data.IDataRecord"/>) and the executor's row loop
/// (which takes a <see cref="DbDataReader"/>), so no second record fake is needed.
/// </summary>
/// <remarks>
/// It records calls as well as serving values — how often each column name was resolved, how often the
/// reader was closed, whether a further result set was ever requested, which token the last read got —
/// because several of the behaviours under test are about the order and the count of those calls rather
/// than about the data that comes back.
/// <para>
/// Like a real reader it starts positioned BEFORE the first row, so a test that hands it straight to a
/// row materialiser must call <see cref="Read"/> or <see cref="ReadAsync(CancellationToken)"/> first;
/// reading a value before then is a clearly worded <see cref="InvalidOperationException"/> rather than a
/// puzzling index error.
/// </para>
/// </remarks>
internal sealed class FakeDbDataReader : DbDataReader
{
    private readonly List<(string[] Columns, object?[][] Rows)> _sets = [];
    private int _set;
    private int _row = -1;
    private bool _closed;

    /// <summary>Creates a reader positioned before the first row of its only result set.</summary>
    internal FakeDbDataReader(string[] columns, params object?[][] rows) => AddResultSet(columns, rows);

    /// <summary>Appends a further result set, reachable only through <see cref="NextResult"/>.</summary>
    internal FakeDbDataReader AddResultSet(string[] columns, params object?[][] rows)
    {
        _sets.Add((columns, rows));
        return this;
    }

    /// <summary>
    /// How many times each name was passed to <see cref="GetOrdinal"/>. Mutable on purpose: a test that
    /// counts lookups per row wants to clear it after the arrange and before the act.
    /// </summary>
    internal Dictionary<string, int> GetOrdinalCalls { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Counts <see cref="CloseAsync"/> specifically. <see cref="DbDataReader.Close"/> is deliberately
    /// left to the base implementation so that disposing the reader in a test cannot inflate the count
    /// that the drain-then-close assertions depend on.
    /// </summary>
    internal int CloseCallCount { get; private set; }

    /// <summary>Counts <see cref="NextResult"/>, including the calls made through <see cref="NextResultAsync"/>.</summary>
    internal int NextResultCallCount { get; private set; }

    /// <summary>Reads attempted after the reader was closed; the close-before-reading-the-RETURN ordering is load bearing.</summary>
    internal int RowsReadAfterClose { get; private set; }

    /// <summary>
    /// The token handed to the most recent <see cref="ReadAsync(CancellationToken)"/>. Recorded before
    /// the cancellation check, so a call that throws still proves the token was forwarded.
    /// </summary>
    internal CancellationToken LastReadToken { get; private set; }

    private bool HasCurrentSet => _set < _sets.Count;

    private string[] Columns => HasCurrentSet ? _sets[_set].Columns : Array.Empty<string>();

    private object?[] Current
    {
        get
        {
            // A real reader throws here too, but with a message about the reader's state rather than an
            // index error out of this fixture's internals; say plainly what the test forgot to do.
            if (!HasCurrentSet || _row < 0 || _row >= _sets[_set].Rows.Length)
                throw new InvalidOperationException(
                    "FakeDbDataReader has no current row: call Read() or ReadAsync() and check that it returned true before reading a value.");
            return _sets[_set].Rows[_row];
        }
    }

    public override int GetOrdinal(string name)
    {
        GetOrdinalCalls[name] = GetOrdinalCalls.TryGetValue(name, out var count) ? count + 1 : 1;

        // Ordinal, not case-insensitive, and IndexOutOfRangeException naming the column: that is the
        // shape SqlDataReader raises for an unknown name, and the result-set-drift test asserts on it.
        var ordinal = Array.IndexOf(Columns, name);
        if (ordinal < 0)
            throw new IndexOutOfRangeException(name);
        return ordinal;
    }

    public override bool Read()
    {
        if (_closed)
            RowsReadAfterClose++;
        if (!HasCurrentSet)
            return false;
        return ++_row < _sets[_set].Rows.Length;
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        LastReadToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read());
    }

    public override bool NextResult()
    {
        NextResultCallCount++;
        _row = -1;
        return ++_set < _sets.Count;
    }

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NextResult());
    }

    public override Task CloseAsync()
    {
        CloseCallCount++;
        _closed = true;
        return Task.CompletedTask;
    }

    public override bool IsClosed => _closed;

    public override int FieldCount => Columns.Length;

    public override bool HasRows => HasCurrentSet && _sets[_set].Rows.Length > 0;

    public override string GetName(int ordinal) => Columns[ordinal];

    public override bool IsDBNull(int ordinal) => Current[ordinal] is null or DBNull;

    public override object GetValue(int ordinal) => Current[ordinal]!;

    // Every typed getter is a plain unboxing cast, so a DBNull cell reaches the caller as an
    // InvalidCastException. That is precisely what the un-guarded non-nullable read path must surface,
    // and softening it here would hide the one failure mode those tests exist to pin.
    public override bool GetBoolean(int ordinal) => (bool)Current[ordinal]!;

    public override byte GetByte(int ordinal) => (byte)Current[ordinal]!;

    public override short GetInt16(int ordinal) => (short)Current[ordinal]!;

    public override int GetInt32(int ordinal) => (int)Current[ordinal]!;

    public override long GetInt64(int ordinal) => (long)Current[ordinal]!;

    public override float GetFloat(int ordinal) => (float)Current[ordinal]!;

    public override double GetDouble(int ordinal) => (double)Current[ordinal]!;

    public override decimal GetDecimal(int ordinal) => (decimal)Current[ordinal]!;

    public override DateTime GetDateTime(int ordinal) => (DateTime)Current[ordinal]!;

    public override Guid GetGuid(int ordinal) => (Guid)Current[ordinal]!;

    public override string GetString(int ordinal) => (string)Current[ordinal]!;

    // Nothing under test reaches the members below. Throwing keeps an accidental call loud instead of
    // letting it return something plausible and silently weaken an assertion.
    public override int Depth => throw Unsupported();

    public override int RecordsAffected => throw Unsupported();

    public override object this[int ordinal] => throw Unsupported();

    public override object this[string name] => throw Unsupported();

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw Unsupported();

    public override char GetChar(int ordinal) => throw Unsupported();

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw Unsupported();

    public override string GetDataTypeName(int ordinal) => throw Unsupported();

    public override Type GetFieldType(int ordinal) => throw Unsupported();

    public override int GetValues(object[] values) => throw Unsupported();

    public override IEnumerator GetEnumerator() => throw Unsupported();

    private static NotSupportedException Unsupported([CallerMemberName] string? member = null) =>
        new($"FakeDbDataReader does not implement {member}; nothing under test calls it.");
}
