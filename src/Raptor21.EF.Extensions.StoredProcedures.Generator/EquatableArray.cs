using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Raptor21.EF.Extensions.StoredProcedures.Generator;

/// <summary>
/// Immutable array wrapper with structural (value-based) equality, so models that contain
/// arrays stay cacheable in the incremental generator pipeline.
/// </summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly T[]? _array;

    public EquatableArray(T[] array) => _array = array;

    public T[] AsArray() => _array ?? Array.Empty<T>();

    public int Count => _array?.Length ?? 0;

    public bool Equals(EquatableArray<T> other) => AsArray().AsSpan().SequenceEqual(other.AsArray());

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        if (_array is null) return 0;
        var hash = 17;
        foreach (var item in _array)
            hash = unchecked(hash * 31 + (item?.GetHashCode() ?? 0));
        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)AsArray()).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => AsArray().GetEnumerator();

    public static EquatableArray<T> From(IEnumerable<T> items) => new(items.ToArray());
}
