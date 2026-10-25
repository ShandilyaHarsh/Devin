using System.Reflection;
using System.Runtime.Loader;
using Barebone.Platform.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Barebone.Platform.Internal;

internal sealed record LoadedApp(AppManifest Manifest, string ManifestYaml, PolicyDocument Policy, IPlatformApp? Code);

/// <summary>Discovers apps under Platform:AppsDirectory. Any invalid manifest/policy/assembly throws (fail closed).</summary>
internal sealed class AppRegistry
{
    public const string PlatformSlug = "platform";

    private readonly Dictionary<string, LoadedApp> apps = new(StringComparer.Ordinal);

    public AppRegistry(IConfiguration configuration, IManifestService manifests)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(manifests);
        AppsDirectory = configuration["Platform:AppsDirectory"] is { Length: > 0 } dir
            ? Path.GetFullPath(dir)
            : Path.Combine(AppContext.BaseDirectory, "apps");
        PlatformPolicy = LoadPlatformPolicy();
        Load(manifests);
        Signature = AppsDirectory + "|" + string.Join(",", apps.Keys.Order(StringComparer.Ordinal));
    }

    public string AppsDirectory { get; }

    public string Signature { get; }

    public PolicyDocument PlatformPolicy { get; }

    public IEnumerable<LoadedApp> Apps => apps.Values;

    public LoadedApp? Get(string slug) => apps.GetValueOrDefault(slug);

    public PolicyDocument? GetPolicy(string slug) =>
        slug == PlatformSlug ? PlatformPolicy : apps.GetValueOrDefault(slug)?.Policy;

    private static PolicyDocument LoadPlatformPolicy()
    {
        using var stream = typeof(AppRegistry).Assembly.GetManifestResourceStream("Barebone.Platform.policy.yaml")
            ?? throw new InvalidOperationException("Platform policy resource missing.");
        using var reader = new StreamReader(stream);
        return PolicyParser.Parse(reader.ReadToEnd(), PlatformSlug, null);
    }

    private void Load(IManifestService manifests)
    {
        if (!Directory.Exists(AppsDirectory))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(AppsDirectory).Order(StringComparer.Ordinal))
        {
            var manifestPath = Path.Combine(dir, "platform.yaml");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            var folder = Path.GetFileName(dir);
            var yaml = File.ReadAllText(manifestPath);
            var validation = manifests.Validate(yaml);
            if (!validation.IsValid)
            {
                throw new ManifestValidationException([.. validation.Errors.Select(e => $"{folder}: {e}")]);
            }

            var manifest = manifests.Parse(yaml);
            if (manifest.Name != folder)
            {
                throw new ManifestValidationException([$"{folder}: name '{manifest.Name}' must equal the folder name"]);
            }

            var policyPath = Path.Combine(dir, "policy.yaml");
            if (!File.Exists(policyPath))
            {
                throw new ManifestValidationException([$"{folder}: policy.yaml is missing"]);
            }

            PolicyDocument policy;
            try
            {
                policy = PolicyParser.Parse(File.ReadAllText(policyPath), manifest.Name, manifest.Roles);
            }
            catch (InvalidOperationException ex)
            {
                throw new ManifestValidationException([$"{folder}: {ex.Message}"]);
            }

            var code = manifest.BackendAssembly is null ? null : LoadCode(manifest);
            apps.Add(manifest.Name, new LoadedApp(manifest, yaml, policy, code));
        }
    }

    private static IPlatformApp LoadCode(AppManifest manifest)
    {
        Assembly assembly;
        try
        {
            assembly = Assembly.Load(new AssemblyName(manifest.BackendAssembly!));
        }
        catch (FileNotFoundException)
        {
            var path = Path.Combine(AppContext.BaseDirectory, manifest.BackendAssembly + ".dll");
            if (!File.Exists(path))
            {
                throw new ManifestValidationException([$"{manifest.Name}: backend assembly '{manifest.BackendAssembly}' not found"]);
            }

            assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        }

        var types = assembly.GetExportedTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IPlatformApp).IsAssignableFrom(t))
            .ToList();
        if (types.Count != 1)
        {
            throw new ManifestValidationException([$"{manifest.Name}: assembly must contain exactly one public IPlatformApp (found {types.Count})"]);
        }

        var app = (IPlatformApp)Activator.CreateInstance(types[0])!;
        if (app.Slug != manifest.Name)
        {
            throw new ManifestValidationException([$"{manifest.Name}: IPlatformApp.Slug '{app.Slug}' does not match the manifest"]);
        }

        return app;
    }
}
