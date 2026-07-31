using ProjectManager.Wpf.Models;

namespace ProjectManager.Wpf.Infrastructure;

public static class ProjectDiscovery
{
    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        "bin",
        "obj",
        "node_modules",
        "dist",
        "build",
        "packages"
    };

    public static IReadOnlyList<ManagedProject> Scan(string rootDirectory, int maximumDepth = 3)
    {
        if (!Directory.Exists(rootDirectory))
        {
            throw new DirectoryNotFoundException($"扫描目录不存在：{rootDirectory}");
        }

        var projects = new Dictionary<string, ManagedProject>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((Path.GetFullPath(rootDirectory), 0));

        while (pending.Count > 0)
        {
            var (directoryPath, depth) = pending.Dequeue();
            var files = GetFileNames(directoryPath);
            var commands = CreateCommands(files);
            if (commands.Count > 0)
            {
                projects[directoryPath] = new ManagedProject
                {
                    Name = new DirectoryInfo(directoryPath).Name,
                    WorkingDirectory = directoryPath,
                    Commands = commands
                };
            }

            if (depth >= maximumDepth)
            {
                continue;
            }

            foreach (var childDirectory in EnumerateDirectories(directoryPath))
            {
                pending.Enqueue((childDirectory, depth + 1));
            }
        }

        return projects.Values
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static List<string> GetFileNames(string directoryPath)
    {
        try
        {
            return Directory.EnumerateFiles(directoryPath)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Select(name => name!)
                .ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return new List<string>();
        }
        catch (DirectoryNotFoundException)
        {
            return new List<string>();
        }
    }

    private static IEnumerable<string> EnumerateDirectories(string directoryPath)
    {
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(directoryPath);
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }
        catch (DirectoryNotFoundException)
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            if (!IgnoredDirectoryNames.Contains(Path.GetFileName(directory)))
            {
                yield return directory;
            }
        }
    }

    private static List<ProjectCommand> CreateCommands(IReadOnlyList<string> files)
    {
        var commands = new List<ProjectCommand>();
        var hasPackageJson = files.Any(file => file.Equals("package.json", StringComparison.OrdinalIgnoreCase));
        var hasCsproj = files.Any(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        var hasSolution = files.Any(file => file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));
        var hasPom = files.Any(file => file.Equals("pom.xml", StringComparison.OrdinalIgnoreCase));
        var hasCompose = files.Any(file =>
            file.Equals("docker-compose.yml", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("docker-compose.yaml", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("compose.yml", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("compose.yaml", StringComparison.OrdinalIgnoreCase));

        if (hasPackageJson)
        {
            commands.Add(CreateCommand("开发", "npm run dev"));
            commands.Add(CreateCommand("构建", "npm run build"));
        }

        if (hasCsproj)
        {
            commands.Add(CreateCommand("运行", "dotnet run"));
            commands.Add(CreateCommand("构建", "dotnet build"));
        }
        else if (hasSolution)
        {
            commands.Add(CreateCommand("构建", "dotnet build"));
        }

        if (hasPom)
        {
            commands.Add(CreateCommand("开发", "mvn spring-boot:run"));
            commands.Add(CreateCommand("测试", "mvn test"));
        }

        if (hasCompose)
        {
            commands.Add(CreateCommand("启动容器", "docker compose up"));
            commands.Add(CreateCommand("停止容器", "docker compose down"));
        }

        return commands;
    }

    private static ProjectCommand CreateCommand(string name, string commandText) => new()
    {
        Name = name,
        CommandText = commandText
    };
}
