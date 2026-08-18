using System.Collections;
using System.Globalization;
using System.Reflection;

namespace Highway.Server.Host.Configuration;

/// <summary>
/// Environment-variable overrides (feature 031, design § Environment Overrides).
/// Every leaf key of the configuration has one variable: <c>HIGHWAY_</c> plus the
/// dotted path with dots as underscores, upper-cased — e.g.
/// <c>server.maxDeliveryAttempts</c> → <c>HIGHWAY_SERVER_MAXDELIVERYATTEMPTS</c>.
/// Two short aliases exist for the values operators actually type:
/// <c>HIGHWAY_PASSWORD</c> and <c>HIGHWAY_ACL_FILE</c>.
///
/// <para>An unknown <c>HIGHWAY_*</c> variable is ignored, not an error: the process
/// environment is shared space (the samples use their own <c>HIGHWAY_*</c> names), and
/// a hard failure there would break innocent shells. The JSON file — which the
/// operator fully owns — is the strict one.</para>
/// </summary>
internal static class EnvironmentOverrides
{
    private const string Prefix = "HIGHWAY_";

    /// <summary>The alias short forms (design § Environment Overrides).</summary>
    internal static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HIGHWAY_PASSWORD"] = "authentication.password",
            ["HIGHWAY_ACL_FILE"] = "authentication.aclFile",
        };

    /// <summary>Leaf path → the property it sets, discovered once by reflection.</summary>
    private static readonly Dictionary<string, Leaf> Leaves = BuildLeaves();

    internal static string EnvironmentName(string path)
        => Prefix + path.Replace('.', '_').ToUpperInvariant();

    /// <summary>Every variable name this loader understands (aliases included).</summary>
    internal static IReadOnlyCollection<string> KnownNames { get; } =
        Leaves.Keys.Select(EnvironmentName).Concat(Aliases.Keys).ToArray();

    /// <summary>Every leaf path in the schema, for conformance tests.</summary>
    internal static IReadOnlyCollection<string> LeafPaths { get; } = Leaves.Keys.ToArray();

    private sealed record Leaf(string[] SectionSteps, PropertyInfo Property);

    /// <summary>
    /// Applies every recognized <c>HIGHWAY_*</c> variable in <paramref name="environment"/>
    /// to <paramref name="configuration"/>. <paramref name="overriddenPaths"/>, when given,
    /// receives the dotted path of each key an override touched — the loader uses that to
    /// resolve relative paths against the right basis (design § D4).
    /// </summary>
    public static void Apply(
        HostConfiguration configuration,
        IDictionary environment,
        HashSet<string>? overriddenPaths = null)
    {
        foreach (DictionaryEntry entry in environment)
        {
            if (entry.Key is not string name || !name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!TryResolvePath(name, out var path))
                continue;   // unknown HIGHWAY_* variables are ignored — see the class doc

            var leaf = Leaves[path];

            object section = configuration;
            foreach (var step in leaf.SectionSteps)
                section = section.GetType().GetProperty(step)?.GetValue(section)
                          ?? throw new InvalidOperationException($"Configuration section '{step}' not found.");

            leaf.Property.SetValue(section, ConvertValue(entry.Value as string, leaf.Property.PropertyType, name));
            overriddenPaths?.Add(path);
        }
    }

    private static bool TryResolvePath(string variableName, out string path)
    {
        if (Aliases.TryGetValue(variableName, out var aliased))
        {
            path = aliased;
            return true;
        }

        foreach (var leafPath in Leaves.Keys)
        {
            if (string.Equals(EnvironmentName(leafPath), variableName, StringComparison.OrdinalIgnoreCase))
            {
                path = leafPath;
                return true;
            }
        }

        path = "";
        return false;
    }

    private static object? ConvertValue(string? raw, Type targetType, string variableName)
    {
        if (raw is null)
            throw new ConfigurationException($"{variableName} has no value.");

        try
        {
            if (targetType == typeof(string))
                return raw;

            if (targetType == typeof(int))
                return int.Parse(raw, CultureInfo.InvariantCulture);

            if (targetType == typeof(long))
                return SizeFormat.Parse(raw, variableName);

            if (targetType == typeof(bool))
                return bool.Parse(raw);

            if (targetType == typeof(TimeSpan))
            {
                // Require the explicit form: a bare number would silently mean days.
                if (!raw.Contains(':') || !TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var ts))
                    throw new ConfigurationException(
                        $"{variableName}: '{raw}' is not a duration. Use \"hh:mm:ss\" (e.g. \"00:05:00\").");
                return ts;
            }

            if (targetType.IsEnum)
                return Enum.Parse(targetType, raw, ignoreCase: true);
        }
        catch (ConfigurationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new ConfigurationException($"{variableName}: '{raw}' is not a valid {targetType.Name}.");
        }

        throw new ConfigurationException($"{variableName}: configuration keys of type {targetType.Name} are not supported.");
    }

    private static Dictionary<string, Leaf> BuildLeaves()
    {
        var leaves = new Dictionary<string, Leaf>(StringComparer.OrdinalIgnoreCase);

        foreach (var sectionProperty in typeof(HostConfiguration).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            WalkSection(sectionProperty.PropertyType, [sectionProperty.Name], [JsonName(sectionProperty.Name)]);
        }

        return leaves;

        // propertySteps are the PascalCase names used to WALK from the HostConfiguration
        // instance to the object owning a leaf; jsonSteps are the camelCase names used to
        // BUILD the environment-variable name and the dotted path.
        void WalkSection(Type type, string[] propertySteps, string[] jsonSteps)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanWrite)
                    continue;

                var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

                var isDictionary = typeof(IDictionary).IsAssignableFrom(propertyType);

                var isSection = !isDictionary
                    && !propertyType.IsPrimitive
                    && propertyType != typeof(string)
                    && propertyType != typeof(TimeSpan)
                    && !propertyType.IsEnum
                    && propertyType.IsClass;

                if (isSection)
                {
                    WalkSection(propertyType, [.. propertySteps, property.Name], [.. jsonSteps, JsonName(property.Name)]);
                    continue;
                }

                if (isDictionary)
                    continue;   // per-name overrides are file-only; one variable cannot address a map

                var dottedPath = string.Join('.', [.. jsonSteps, JsonName(property.Name)]);
                leaves[dottedPath] = new Leaf(propertySteps, property);
            }
        }
    }

    /// <summary>The JSON/env name of a PascalCase member: camelCase.</summary>
    private static string JsonName(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
