using System.Reflection;
using System.Text;

namespace Raptor21.EF.Extensions.StoredProcedures.Tests;

/// <summary>
/// An <see cref="Assembly"/> whose manifest resources are supplied in memory, so resource discovery can
/// be exercised with no embedded .sql file anywhere in the test project.
/// </summary>
/// <remarks>
/// <see cref="Assembly"/> declares <see cref="GetManifestResourceNames"/> and
/// <see cref="GetManifestResourceStream(string)"/> as virtual, so a subclass gets exact control over the
/// resource names, the order they enumerate in and their raw bytes. Controlling the bytes is what makes
/// the byte-order-mark case testable, and controlling the stream is what makes the null-stream case —
/// which the documented contract of <see cref="GetManifestResourceStream(string)"/> permits — reachable
/// at all.
/// </remarks>
internal sealed class FakeResourceAssembly : Assembly
{
    private readonly List<(string Name, byte[]? Bytes)> _resources = [];

    /// <summary>Adds a resource holding <paramref name="content"/> as UTF-8 with no byte-order mark.</summary>
    internal FakeResourceAssembly With(string name, string content)
    {
        _resources.Add((name, Encoding.UTF8.GetBytes(content)));
        return this;
    }

    /// <summary>Adds a resource from raw bytes, for the cases whose whole point is the exact byte sequence.</summary>
    internal FakeResourceAssembly WithBytes(string name, byte[] bytes)
    {
        _resources.Add((name, bytes));
        return this;
    }

    /// <summary>Adds a name that enumerates but whose stream comes back null.</summary>
    internal FakeResourceAssembly WithNullStream(string name)
    {
        _resources.Add((name, null));
        return this;
    }

    /// <summary>
    /// Names come back in the order they were added, because one test turns on which of two names that
    /// collide after prefix-stripping was yielded last.
    /// </summary>
    public override string[] GetManifestResourceNames() => _resources.Select(r => r.Name).ToArray();

    public override Stream? GetManifestResourceStream(string name)
    {
        foreach (var (resourceName, bytes) in _resources)
        {
            // Manifest resource names are case-sensitive, and two names differing only in case are two
            // distinct resources, so the lookup has to be ordinal.
            if (!string.Equals(resourceName, name, StringComparison.Ordinal))
                continue;

            // A fresh stream per call, matching a real assembly; null for a name added through
            // WithNullStream, which is indistinguishable here from a name that was never added.
            return bytes is null ? null : new MemoryStream(bytes, writable: false);
        }

        return null;
    }

    /// <summary>
    /// <see cref="Assembly.FullName"/> throws by default and <see cref="Assembly.ToString"/> reads it, so
    /// without this an assertion failure message would itself fail while being formatted.
    /// </summary>
    public override string? FullName => nameof(FakeResourceAssembly);
}
