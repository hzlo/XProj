using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using XProj.Plugin.Abstractions;

namespace ProjectManager.Wpf.Infrastructure;

public sealed class PluginLoader
{
    public const int CurrentApiVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IReadOnlyList<string> _pluginDirectories;
    private readonly Version _hostVersion;

    public PluginLoader(IEnumerable<string> pluginDirectories, Version hostVersion)
    {
        _pluginDirectories = pluginDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _hostVersion = NormalizeVersion(hostVersion);
    }

    public PluginLoadResult Load()
    {
        var loaded = new List<LoadedPlugin>();
        var failures = new List<PluginLoadFailure>();
        var loadedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packageDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pluginDirectory in _pluginDirectories)
        {
            if (!Directory.Exists(pluginDirectory))
            {
                continue;
            }

            foreach (var manifestPath in Directory.EnumerateFiles(pluginDirectory, "plugin.json", SearchOption.AllDirectories))
            {
                var packageDirectory = Path.GetDirectoryName(manifestPath)!;
                if (!packageDirectories.Add(packageDirectory))
                {
                    continue;
                }

                TryLoadPackage(packageDirectory, manifestPath, loaded, failures, loadedIds);
            }

            foreach (var assemblyPath in Directory.EnumerateFiles(pluginDirectory, "*.dll", SearchOption.AllDirectories))
            {
                var packageDirectory = Path.GetDirectoryName(assemblyPath)!;
                if (packageDirectories.Contains(packageDirectory))
                {
                    continue;
                }

                packageDirectories.Add(packageDirectory);
                TryLoadPackage(packageDirectory, manifestPath: null, loaded, failures, loadedIds, assemblyPath);
            }
        }

        return new PluginLoadResult(loaded, failures);
    }

    private void TryLoadPackage(
        string packageDirectory,
        string? manifestPath,
        ICollection<LoadedPlugin> loaded,
        ICollection<PluginLoadFailure> failures,
        ISet<string> loadedIds,
        string? fallbackAssemblyPath = null)
    {
        try
        {
            var manifest = ReadManifest(manifestPath);
            ValidateManifest(manifest);

            var assemblyPath = ResolveAssemblyPath(packageDirectory, manifest, fallbackAssemblyPath);
            var loadContext = new PluginLoadContext(assemblyPath);
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var pluginTypes = FindPluginTypes(assembly, manifest.EntryType);
            if (pluginTypes.Count == 0)
            {
                throw new InvalidDataException($"程序集 {Path.GetFileName(assemblyPath)} 未找到 IXProjPlugin 实现。");
            }

            foreach (var pluginType in pluginTypes)
            {
                if (Activator.CreateInstance(pluginType) is not IXProjPlugin plugin)
                {
                    continue;
                }

                ValidatePlugin(plugin, manifest);
                if (!loadedIds.Add(plugin.Id))
                {
                    throw new InvalidDataException($"插件 Id 重复：{plugin.Id}。");
                }

                var resolvedManifest = new PluginManifest
                {
                    Id = plugin.Id,
                    Name = string.IsNullOrWhiteSpace(manifest.Name) ? plugin.Name : manifest.Name,
                    Description = string.IsNullOrWhiteSpace(manifest.Description) ? plugin.Description : manifest.Description,
                    Version = string.IsNullOrWhiteSpace(manifest.Version) ? plugin.Version : manifest.Version,
                    ApiVersion = manifest.ApiVersion,
                    EntryAssembly = Path.GetFileName(assemblyPath),
                    EntryType = pluginType.FullName ?? pluginType.Name,
                    MinHostVersion = manifest.MinHostVersion,
                    DefaultEnabled = manifest.DefaultEnabled
                };
                loaded.Add(new LoadedPlugin(plugin, resolvedManifest, packageDirectory, loadContext));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            failures.Add(new PluginLoadFailure(packageDirectory, exception.Message));
        }
    }

    private static PluginManifest ReadManifest(string? manifestPath)
    {
        if (manifestPath is null)
        {
            return new PluginManifest();
        }

        using var stream = File.OpenRead(manifestPath);
        return JsonSerializer.Deserialize<PluginManifest>(stream, SerializerOptions)
            ?? throw new InvalidDataException("plugin.json 内容为空。");
    }

    private static string ResolveAssemblyPath(string packageDirectory, PluginManifest manifest, string? fallbackAssemblyPath)
    {
        var assemblyPath = string.IsNullOrWhiteSpace(manifest.EntryAssembly)
            ? fallbackAssemblyPath
            : Path.Combine(packageDirectory, manifest.EntryAssembly);
        if (string.IsNullOrWhiteSpace(assemblyPath))
        {
            throw new InvalidDataException("插件没有声明入口程序集。");
        }

        var fullPath = Path.GetFullPath(assemblyPath);
        var fullPackagePath = EnsureTrailingDirectorySeparator(Path.GetFullPath(packageDirectory));
        if (!fullPath.StartsWith(fullPackagePath, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            throw new InvalidDataException("插件入口程序集路径无效。");
        }

        return fullPath;
    }

    private void ValidateManifest(PluginManifest manifest)
    {
        if (manifest.ApiVersion != CurrentApiVersion)
        {
            throw new InvalidDataException($"插件 API 版本 {manifest.ApiVersion} 与宿主 API 版本 {CurrentApiVersion} 不兼容。");
        }

        if (!string.IsNullOrWhiteSpace(manifest.MinHostVersion) &&
            Version.TryParse(manifest.MinHostVersion, out var minHostVersion) &&
            NormalizeVersion(minHostVersion) > _hostVersion)
        {
            throw new InvalidDataException($"插件要求宿主版本至少为 {manifest.MinHostVersion}。");
        }
    }

    private static void ValidatePlugin(IXProjPlugin plugin, PluginManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(plugin.Id) || string.IsNullOrWhiteSpace(plugin.Name))
        {
            throw new InvalidDataException("插件 Id 和名称不能为空。");
        }

        if (!string.IsNullOrWhiteSpace(manifest.Id) &&
            !string.Equals(manifest.Id, plugin.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"清单 Id {manifest.Id} 与插件 Id {plugin.Id} 不一致。");
        }

        if (!Version.TryParse(plugin.Version, out _) ||
            !string.IsNullOrWhiteSpace(manifest.Version) && !Version.TryParse(manifest.Version, out _))
        {
            throw new InvalidDataException($"插件 {plugin.Id} 的版本号无效。");
        }

        if (!string.IsNullOrWhiteSpace(manifest.Version) &&
            Version.TryParse(plugin.Version, out var pluginVersion) &&
            Version.TryParse(manifest.Version, out var manifestVersion) &&
            NormalizeVersion(pluginVersion) != NormalizeVersion(manifestVersion))
        {
            throw new InvalidDataException($"插件 {plugin.Id} 的代码版本与清单版本不一致。");
        }
    }

    private static IReadOnlyList<Type> FindPluginTypes(Assembly assembly, string entryType)
    {
        var types = GetLoadableTypes(assembly)
            .Where(type => typeof(IXProjPlugin).IsAssignableFrom(type) &&
                          type is { IsAbstract: false, IsInterface: false } &&
                          (string.IsNullOrWhiteSpace(entryType) || string.Equals(type.FullName, entryType, StringComparison.Ordinal)))
            .ToArray();
        return types;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null)!;
        }
    }

    private static Version NormalizeVersion(Version version) =>
        new(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));

    private static string EnsureTrailingDirectorySeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
}

public sealed record LoadedPlugin(
    IXProjPlugin Plugin,
    PluginManifest Manifest,
    string PackageDirectory,
    AssemblyLoadContext LoadContext);

public sealed record PluginLoadFailure(string PackageDirectory, string Message);

public sealed class PluginLoadResult(
    IReadOnlyList<LoadedPlugin> plugins,
    IReadOnlyList<PluginLoadFailure> failures)
{
    public IReadOnlyList<LoadedPlugin> Plugins { get; } = plugins;
    public IReadOnlyList<PluginLoadFailure> Failures { get; } = failures;
}

internal sealed class PluginLoadContext(string mainAssemblyPath) : AssemblyLoadContext(isCollectible: true)
{
    private static readonly HashSet<string> SharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        typeof(IXProjPlugin).Assembly.GetName().Name!,
        "Material.Icons.WPF",
        "PresentationCore",
        "PresentationFramework",
        "WindowsBase",
        "System.Xaml"
    };

    private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && SharedAssemblies.Contains(assemblyName.Name))
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName);
        }

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return assemblyPath is null ? null : LoadFromAssemblyPath(assemblyPath);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(libraryPath);
    }
}
