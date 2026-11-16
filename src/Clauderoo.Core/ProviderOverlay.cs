using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Clauderoo.Core;

public record Overlay(IReadOnlyDictionary<string, string> Env, IReadOnlyDictionary<string, string> Top);

public static partial class ProviderOverlay
{
    public static IReadOnlySet<string> ReservedEnv { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "CLAUDE_CODE_USE_VERTEX",
        "ANTHROPIC_VERTEX_PROJECT_ID",
        "CLOUD_ML_REGION",
        "ANTHROPIC_DEFAULT_SONNET_MODEL",
        "ANTHROPIC_DEFAULT_OPUS_MODEL",
        "ANTHROPIC_DEFAULT_HAIKU_MODEL",
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
    };

    public static IReadOnlySet<string> ReservedTop { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "apiKeyHelper",
        "gcpAuthRefresh",
    };

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\z")]
    private static partial Regex NameRegex();

    public static void ValidateEnvName(string name)
    {
        if (!NameRegex().IsMatch(name))
            throw new ClauderooException($"Invalid environment variable name: {name}");
        if (ReservedEnv.Contains(name))
            throw new ClauderooException($"Environment variable {name} is managed by the provider and cannot be set.");
    }

    public static Overlay Build(string profile, ProfileEntry e, string helperExe)
    {
        // Reserved names come only from the provider; a hand-edited config must never inject them.
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, val) in e.Env)
            if (!ReservedEnv.Contains(k)) env[k] = val;
        var top = new Dictionary<string, string>(StringComparer.Ordinal);

        switch (Providers.Of(e))
        {
            case Providers.Vertex:
                var v = e.Vertex ?? new VertexSettings();
                env["CLAUDE_CODE_USE_VERTEX"] = "1";
                env["ANTHROPIC_VERTEX_PROJECT_ID"] = v.Project;
                env["CLOUD_ML_REGION"] = v.Region;
                if (!string.IsNullOrWhiteSpace(v.SonnetModel)) env["ANTHROPIC_DEFAULT_SONNET_MODEL"] = v.SonnetModel;
                if (!string.IsNullOrWhiteSpace(v.OpusModel)) env["ANTHROPIC_DEFAULT_OPUS_MODEL"] = v.OpusModel;
                if (!string.IsNullOrWhiteSpace(v.HaikuModel)) env["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = v.HaikuModel;
                top["gcpAuthRefresh"] = "gcloud auth application-default login";
                break;
            case Providers.ApiKey:
                top["apiKeyHelper"] = $"\"{helperExe}\" apikey {profile}";
                break;
        }

        return new Overlay(env, top);
    }

    public static JsonObject Apply(
        JsonObject settings,
        Overlay o,
        IReadOnlyCollection<string> previouslyManagedEnv,
        IReadOnlyCollection<string> previouslyManagedTop)
    {
        var result = (JsonObject)settings.DeepClone();

        var existing = result["env"];
        if (existing is not null && existing is not JsonObject)
            throw new ClauderooException("settings.json \"env\" is not an object.");

        var envObj = existing as JsonObject;
        var created = false;

        if (envObj is not null)
        {
            foreach (var k in ReservedEnv.Concat(previouslyManagedEnv)) envObj.Remove(k);
        }

        foreach (var k in ReservedTop.Concat(previouslyManagedTop)) result.Remove(k);

        if (o.Env.Count > 0 && envObj is null)
        {
            envObj = new JsonObject();
            result["env"] = envObj;
            created = true;
        }

        foreach (var (k, val) in o.Env) envObj![k] = val;
        foreach (var (k, val) in o.Top) result[k] = val;

        if (created && envObj!.Count == 0) result.Remove("env");
        return result;
    }
}

public static class HelperExePath
{
    /// <summary>
    /// The exe that apiKeyHelper runs: the installed clauderoo.exe if present, else clauderoo.exe next to the running
    /// assembly (never Environment.ProcessPath, which is clauderoow.exe when the tray syncs).
    /// </summary>
    public static string Resolve() =>
        Resolve(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(AppContext.BaseDirectory, "clauderoo.exe"));

    public static string Resolve(string localAppData, string fallbackExe)
    {
        var installed = Path.Combine(localAppData, "clauderoo", "clauderoo.exe");
        return File.Exists(installed) ? installed : fallbackExe;
    }
}
