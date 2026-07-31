using System.Text.Json;
using ProjectManager.Wpf.Models;

namespace ProjectManager.Wpf.Infrastructure;

public sealed class JsonDataStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public JsonDataStore(string? dataDirectory = null)
    {
        var appDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        DataDirectory = dataDirectory ?? Path.Combine(appDataDirectory, "ProjectManagerWpf");
        DataFilePath = Path.Combine(DataDirectory, "data.json");
    }

    public string DataDirectory { get; }
    public string DataFilePath { get; }

    public async Task<AppData> LoadAsync()
    {
        Directory.CreateDirectory(DataDirectory);
        if (!File.Exists(DataFilePath))
        {
            return new AppData();
        }

        try
        {
            await using var stream = File.OpenRead(DataFilePath);
            var data = await JsonSerializer.DeserializeAsync<AppData>(stream, SerializerOptions);
            return Normalize(data ?? new AppData());
        }
        catch (JsonException)
        {
            var backupPath = Path.Combine(DataDirectory, $"data.invalid-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(DataFilePath, backupPath, overwrite: false);
            return new AppData();
        }
    }

    public async Task SaveAsync(AppData data)
    {
        await _saveLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(DataDirectory);
            CreateDailyBackup();
            var temporaryPath = DataFilePath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, data, SerializerOptions);
            }

            File.Move(temporaryPath, DataFilePath, overwrite: true);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private void CreateDailyBackup()
    {
        if (!File.Exists(DataFilePath))
        {
            return;
        }

        var backupPath = Path.Combine(DataDirectory, $"data.backup-{DateTime.Now:yyyyMMdd}.json");
        if (!File.Exists(backupPath))
        {
            File.Copy(DataFilePath, backupPath);
        }

        var backupFiles = Directory
            .EnumerateFiles(DataDirectory, "data.backup-*.json")
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .Skip(7)
            .ToArray();
        foreach (var backupFile in backupFiles)
        {
            File.Delete(backupFile);
        }
    }

    public async Task ExportAsync(AppData data, string filePath)
    {
        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, data, SerializerOptions);
    }

    public async Task<AppData> ImportAsync(string filePath)
    {
        try
        {
            await using var stream = File.OpenRead(filePath);
            var data = await JsonSerializer.DeserializeAsync<AppData>(stream, SerializerOptions);
            if (data is null)
            {
                throw new InvalidDataException("配置文件内容为空或格式不正确。");
            }

            return Normalize(data);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("配置文件不是有效的 XProj JSON 配置。", exception);
        }
    }

    private static AppData Normalize(AppData data)
    {
        data.Settings ??= new AppSettings();
        if (data.Settings.Theme is not ("Dark" or "Light"))
        {
            data.Settings.Theme = "Dark";
        }
        if (data.Settings.CloseBehavior is not ("MinimizeToTray" or "Exit"))
        {
            data.Settings.CloseBehavior = "MinimizeToTray";
        }
        if (string.IsNullOrWhiteSpace(data.Settings.UiFontFamily))
        {
            data.Settings.UiFontFamily = "Microsoft YaHei UI";
        }
        if (!double.IsFinite(data.Settings.UiFontSize) || data.Settings.UiFontSize < 10 || data.Settings.UiFontSize > 24)
        {
            data.Settings.UiFontSize = 13;
        }
        if (string.IsNullOrWhiteSpace(data.Settings.LogFontFamily))
        {
            data.Settings.LogFontFamily = "Consolas";
        }
        if (!double.IsFinite(data.Settings.LogFontSize) || data.Settings.LogFontSize < 8 || data.Settings.LogFontSize > 40)
        {
            data.Settings.LogFontSize = 11;
        }
        if (data.Settings.LogVisibleLineCount is not (100 or 300 or 500 or 1000))
        {
            data.Settings.LogVisibleLineCount = 300;
        }

        data.Groups ??= new List<ProjectGroup>();
        data.Projects ??= new List<ManagedProject>();
        data.RunPlans ??= new List<RunPlan>();
        foreach (var project in data.Projects)
        {
            project.Commands ??= new List<ProjectCommand>();
            project.HealthCheckUrl ??= string.Empty;
            foreach (var command in project.Commands)
            {
                if (command.Shell is not ("Cmd" or "PowerShell"))
                {
                    command.Shell = "Cmd";
                }

                command.EnvironmentVariables ??= string.Empty;
            }
        }

        foreach (var runPlan in data.RunPlans)
        {
            runPlan.Commands ??= new List<RunPlanCommand>();
            if (string.IsNullOrWhiteSpace(runPlan.Name))
            {
                runPlan.Name = "运行方案";
            }
        }

        return data;
    }
}
