using System.Reflection;

namespace Highway.Server.Dashboard;

internal static class EmbeddedResources
{
    private static readonly Assembly Assembly = typeof(EmbeddedResources).Assembly;

    public static string GetIndex() => ReadResource("index.html");
    public static string GetCss() => ReadResource("app.css");
    public static string GetJs() => ReadResource("app.js");

    private static string ReadResource(string name)
    {
        var fullName = Assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));

        if (fullName is null) return $"<!-- resource {name} not found -->";

        using var stream = Assembly.GetManifestResourceStream(fullName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
