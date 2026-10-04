using System.Reflection;
using System.Text;

namespace Raptor21.EF.Extensions.Migrations;

/// <summary>The one reader of embedded <c>.sql</c> resources, shared by every registration entry point.</summary>
internal static class EmbeddedScripts
{
    /// <summary>
    /// Every embedded <c>.sql</c> resource under <paramref name="resourcePrefix"/>, in the assembly's
    /// enumeration order, as (resource name, name relative to the prefix, text with any byte-order mark removed).
    /// </summary>
    public static IEnumerable<(string ResourceName, string ScriptName, string Sql)> Read(Assembly assembly, string resourcePrefix)
    {
        var prefix = resourcePrefix.TrimEnd('.') + ".";

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !resourceName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                continue;

            // GetManifestResourceStream is documented to return null, and the assembly is a caller-supplied
            // parameter, so the null-forgiving operator that used to stand here turned a reachable failure
            // into a NullReferenceException from inside StreamReader - a message naming neither the
            // resource nor where it came from, thrown from the one method whose job is loading resources.
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded resource '{resourceName}' is listed by assembly '{assembly.FullName}' but has no stream.");
            using var reader = new StreamReader(stream, Encoding.UTF8);

            yield return (resourceName, resourceName[prefix.Length..], reader.ReadToEnd());
        }
    }
}
