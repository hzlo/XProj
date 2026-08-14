using System.Text;
using ProjectManager.Wpf.Infrastructure;
using ProjectManager.Wpf.Models;
using ProjectManager.Wpf.ViewModels;

if (args.Contains("--emit-unicode-output"))
{
    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    Console.WriteLine("认证授权中心启动成功 (◐_◐) ✨");
    return;
}

if (args.Length == 2 && args[0] == "--delay-output" && int.TryParse(args[1], out var delayMilliseconds))
{
    await Task.Delay(delayMilliseconds);
    Console.WriteLine("Delayed command completed.");
    return;
}

if (args.Length == 2 && args[0] == "--emit-bulk-output" && int.TryParse(args[1], out var bulkLineCount))
{
    var output = new StringBuilder(bulkLineCount * 80);
    for (var index = 0; index < bulkLineCount; index++)
    {
        output.Append("bulk-").Append(index.ToString("D5")).Append('-').Append('x', 64).AppendLine();
    }

    Console.Write(output.ToString());
    return;
}

var rollingLog = new RollingLogBuffer(100, 70);
var displayedRollingLog = string.Empty;
foreach (var text in Enumerable.Range(0, 30).Select(index => $"line-{index:D2}-value{Environment.NewLine}"))
{
    var change = rollingLog.Append(text);
    displayedRollingLog = displayedRollingLog[change.CharactersToRemove..] + change.TextToAppend;
    if (displayedRollingLog != rollingLog.ToString())
    {
        throw new InvalidOperationException("Incremental rolling log update diverged from retained log content.");
    }
}

var oversizedChange = rollingLog.Append(new string('z', 200) + Environment.NewLine);
displayedRollingLog = displayedRollingLog[oversizedChange.CharactersToRemove..] + oversizedChange.TextToAppend;
if (displayedRollingLog != rollingLog.ToString() || displayedRollingLog.Length > 100)
{
    throw new InvalidOperationException("Oversized rolling log update was not trimmed correctly.");
}
var rollingLogTail = rollingLog.GetTailText(40);
if (rollingLogTail.Length > 40 || !displayedRollingLog.EndsWith(rollingLogTail, StringComparison.Ordinal))
{
    throw new InvalidOperationException("Rolling log tail window was not retained correctly.");
}

if (new AppSettings().LogVisibleLineCount != 300)
{
    throw new InvalidOperationException("The default visible log line count must be 300.");
}
if (!ThemeManager.TryNormalizeColor("#aBc123", out var normalizedColor) || normalizedColor != "#ABC123" ||
    ThemeManager.TryNormalizeColor("ABC123", out _) ||
    !ThemeManager.HasReadableContrast("#1D1D1F", "#DCDDE1") ||
    ThemeManager.HasReadableContrast("#777777", "#888888"))
{
    throw new InvalidOperationException("Theme color parsing or contrast validation failed.");
}

var environmentMarkerName = $"XPROJ_SMOKE_{Guid.NewGuid():N}";
const string EnvironmentMarkerValue = "process-environment-value";
Environment.SetEnvironmentVariable(environmentMarkerName, EnvironmentMarkerValue);
try
{
    var refreshedStartInfo = new System.Diagnostics.ProcessStartInfo();
    SystemEnvironment.Refresh(refreshedStartInfo);
    if (refreshedStartInfo.Environment[environmentMarkerName] != EnvironmentMarkerValue ||
        string.IsNullOrWhiteSpace(refreshedStartInfo.Environment["SystemRoot"]) ||
        string.IsNullOrWhiteSpace(refreshedStartInfo.Environment["Path"]))
    {
        throw new InvalidOperationException("The refreshed Windows environment block is incomplete.");
    }
}
finally
{
    Environment.SetEnvironmentVariable(environmentMarkerName, null);
}

var retainedStressLog = new RollingLogBuffer(500_000, 400_000);
var displayedStressLog = new RollingLineBuffer(300, 76_800, 61_440);
var displayedStressText = string.Empty;
for (var batchIndex = 0; batchIndex < 200; batchIndex++)
{
    var batch = new StringBuilder();
    for (var lineIndex = 0; lineIndex < 100; lineIndex++)
    {
        batch.Append("display-").Append(batchIndex).Append('-').Append(lineIndex).Append('-').Append('x', 64).AppendLine();
    }

    var batchText = batch.ToString();
    retainedStressLog.Append(batchText);
    var change = displayedStressLog.Append(batchText);
    displayedStressText = displayedStressText[change.CharactersToRemove..] + change.TextToAppend;
    if (displayedStressLog.LineCount > 300 || displayedStressText != displayedStressLog.ToString())
    {
        throw new InvalidOperationException("Displayed log stress window exceeded its rendering limit.");
    }
}
var retainedStressTail = retainedStressLog.GetTailLines(300);
if (!displayedStressText.Contains("display-199-99-", StringComparison.Ordinal) ||
    displayedStressText != retainedStressTail)
{
    throw new InvalidOperationException("Displayed log stress window did not retain the newest output.");
}
foreach (var visibleLineCount in new[] { 100, 300, 500, 1000 })
{
    var maximumCharacters = Math.Max(60_000, visibleLineCount * 256);
    var configuredDisplayLog = new RollingLineBuffer(
        visibleLineCount,
        maximumCharacters,
        maximumCharacters * 4 / 5);
    configuredDisplayLog.Append(retainedStressLog.ToString());
    if (configuredDisplayLog.LineCount != visibleLineCount ||
        configuredDisplayLog.ToString() != retainedStressLog.GetTailLines(visibleLineCount))
    {
        throw new InvalidOperationException($"Visible log line limit {visibleLineCount} was not applied exactly.");
    }
}

var newerReleaseChecker = new UpdateChecker(
    new HttpClient(new StaticReleaseRedirectHandler("https://github.com/hzlo/XProj/releases/tag/v1.2.0")),
    new Version(1, 1, 0, 0));
var newerRelease = await newerReleaseChecker.CheckAsync(forceRefresh: true);
if (!newerRelease.IsUpdateAvailable ||
    newerRelease.CurrentVersion != "1.1.0" ||
    newerRelease.LatestVersion != "1.2.0" ||
    newerRelease.ReleaseUrl != "https://github.com/hzlo/XProj/releases/tag/v1.2.0")
{
    throw new InvalidOperationException("Newer GitHub release was not detected correctly.");
}

var currentReleaseChecker = new UpdateChecker(
    new HttpClient(new StaticReleaseRedirectHandler("https://github.com/hzlo/XProj/releases/tag/v1.1.0")),
    new Version(1, 1, 0));
var currentRelease = await currentReleaseChecker.CheckAsync(forceRefresh: true);
if (currentRelease.IsUpdateAvailable ||
    currentRelease.ReleaseUrl != "https://github.com/hzlo/XProj/releases/tag/v1.1.0")
{
    throw new InvalidOperationException("Current release redirect was not handled correctly.");
}

var cachePath = Path.Combine(Path.GetTempPath(), $"xproj-update-cache-{Guid.NewGuid():N}.json");
var cacheHandler = new StaticReleaseRedirectHandler("https://github.com/hzlo/XProj/releases/tag/v1.2.0");
try
{
    var cachedReleaseChecker = new UpdateChecker(
        new HttpClient(cacheHandler),
        new Version(1, 1, 0),
        cachePath);
    await cachedReleaseChecker.CheckAsync(forceRefresh: true);
    await cachedReleaseChecker.CheckAsync();
    if (cacheHandler.RequestCount != 1)
    {
        throw new InvalidOperationException("Automatic update checks did not use the 24-hour cache.");
    }
}
finally
{
    if (File.Exists(cachePath))
    {
        File.Delete(cachePath);
    }
}

var persistenceDirectory = Path.Combine(AppContext.BaseDirectory, "smoke-data");
var dataStore = new JsonDataStore(persistenceDirectory);
var parentGroup = new ProjectGroup { Name = "Parent" };
var childGroup = new ProjectGroup { Name = "Child", ParentId = parentGroup.Id };
var persistedData = new AppData
{
    Settings = new AppSettings
    {
        Theme = "Light",
        LightForegroundColor = "#202124",
        LightBackgroundColor = "#D8DADC",
        DarkForegroundColor = "#C4CCEA",
        DarkBackgroundColor = "#111318",
        CloseBehavior = "Exit",
        UiFontFamily = "Microsoft YaHei UI",
        UiFontSize = 15,
        LogFontFamily = "Consolas",
        LogFontSize = 14,
        LogFontBold = true,
        LogFontItalic = true,
        LogVisibleLineCount = 500
    },
    Groups = new List<ProjectGroup> { parentGroup, childGroup },
    Projects = new List<ManagedProject>
    {
        new()
        {
            Name = "Persisted project",
            WorkingDirectory = AppContext.BaseDirectory,
            GroupId = childGroup.Id,
            Commands = new List<ProjectCommand>
            {
                new() { Name = "Version", CommandText = "dotnet --version" }
            }
        }
    }
};
await dataStore.SaveAsync(persistedData);
var persistedJson = await File.ReadAllTextAsync(dataStore.DataFilePath);
if (persistedJson.Contains("\"description\"", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Removed project description field was persisted.");
}

var legacyJson = System.Text.Json.Nodes.JsonNode.Parse(persistedJson)?.AsObject()
    ?? throw new InvalidOperationException("Persisted configuration could not be parsed for legacy field testing.");
var legacyProject = legacyJson["projects"]?.AsArray().SingleOrDefault()?.AsObject()
    ?? throw new InvalidOperationException("Persisted project was missing for legacy field testing.");
legacyProject["isFavorite"] = true;
legacyProject["lastUsedAt"] = "2026-08-14T12:00:00+08:00";
var legacyImportPath = Path.Combine(persistenceDirectory, "legacy-config.json");
await File.WriteAllTextAsync(legacyImportPath, legacyJson.ToJsonString());
var legacyImportedData = await dataStore.ImportAsync(legacyImportPath);
if (legacyImportedData.Projects.Single().Name != "Persisted project")
{
    throw new InvalidOperationException("Legacy project fields prevented configuration import.");
}

var legacyExportPath = Path.Combine(persistenceDirectory, "legacy-exported-config.json");
await dataStore.ExportAsync(legacyImportedData, legacyExportPath);
var legacyExportedJson = await File.ReadAllTextAsync(legacyExportPath);
if (legacyExportedJson.Contains("\"isFavorite\"", StringComparison.OrdinalIgnoreCase) ||
    legacyExportedJson.Contains("\"lastUsedAt\"", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Removed recent or favorite project fields were persisted after legacy import.");
}

var loadedData = await dataStore.LoadAsync();
if (loadedData.Groups.Count != 2 ||
    loadedData.Projects.Single().GroupId != childGroup.Id ||
    loadedData.Settings.Theme != "Light" ||
    loadedData.Settings.LightForegroundColor != "#202124" ||
    loadedData.Settings.LightBackgroundColor != "#D8DADC" ||
    loadedData.Settings.DarkForegroundColor != "#C4CCEA" ||
    loadedData.Settings.DarkBackgroundColor != "#111318" ||
    loadedData.Settings.CloseBehavior != "Exit" ||
    loadedData.Settings.UiFontFamily != "Microsoft YaHei UI" ||
    loadedData.Settings.UiFontSize != 15 ||
    loadedData.Settings.LogFontFamily != "Consolas" ||
    loadedData.Settings.LogFontSize != 14 ||
    !loadedData.Settings.LogFontBold ||
    !loadedData.Settings.LogFontItalic ||
    loadedData.Settings.LogVisibleLineCount != 500)
{
    throw new InvalidOperationException("Nested group or log font persistence smoke test failed.");
}

var exportPath = Path.Combine(persistenceDirectory, "exported-config.json");
await dataStore.ExportAsync(loadedData, exportPath);
var importedData = await dataStore.ImportAsync(exportPath);
if (importedData.Settings.Theme != "Light" ||
    importedData.Settings.LightForegroundColor != "#202124" ||
    importedData.Settings.DarkBackgroundColor != "#111318" ||
    importedData.Settings.CloseBehavior != "Exit" ||
    importedData.Settings.UiFontFamily != "Microsoft YaHei UI" ||
    importedData.Settings.LogVisibleLineCount != 500 ||
    importedData.Projects.Single().Name != "Persisted project")
{
    throw new InvalidOperationException("Configuration export/import smoke test failed.");
}

var processManager = new ProcessManager();
var outputLines = new List<string>();
var exitCompletion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
var dotnetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
if (!File.Exists(dotnetPath))
{
    throw new FileNotFoundException("The quoted-path smoke test requires dotnet.exe.", dotnetPath);
}

var command = new ProjectCommand
{
    Name = "Smoke test",
    CommandText = $"\"{dotnetPath}\" \"{typeof(Program).Assembly.Location}\" --emit-unicode-output"
};
var project = new ManagedProject
{
    Name = "Smoke test",
    WorkingDirectory = AppContext.BaseDirectory,
    Commands = new List<ProjectCommand> { command }
};

processManager.OutputReceived += (_, eventArgs) =>
{
    if (eventArgs.CommandId == command.Id)
    {
        outputLines.Add(eventArgs.Text);
    }
};
processManager.ProcessExited += (_, eventArgs) =>
{
    if (eventArgs.CommandId == command.Id)
    {
        exitCompletion.TrySetResult(eventArgs.ExitCode);
    }
};

await processManager.StartAsync(project, command);
var exitCode = await exitCompletion.Task.WaitAsync(TimeSpan.FromSeconds(15));
await processManager.DisposeAsync();

if (exitCode != 0)
{
    throw new InvalidOperationException($"Command exited with code {exitCode}. Output: {string.Join(" | ", outputLines)}");
}

if (!outputLines.Contains("认证授权中心启动成功 (◐_◐) ✨"))
{
    throw new InvalidOperationException($"Expected Unicode UTF-8 output was not captured. Output: {string.Join(" | ", outputLines)}");
}

var shellLine = outputLines
    .SelectMany(line => line.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
    .SingleOrDefault(line => line.StartsWith("[Shell] ", StringComparison.Ordinal));
if (shellLine is null)
{
    throw new InvalidOperationException("Expected selected shell information was not captured.");
}

var commandPromptPath = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
if (!shellLine.Equals($"[Shell] {commandPromptPath}", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException($"Expected cmd.exe to be used. Actual: {shellLine}");
}

var concurrentManager = new ProcessManager();
var slowCommand = new ProjectCommand
{
    Name = "Slow command",
    CommandText = $"\"{dotnetPath}\" \"{typeof(Program).Assembly.Location}\" --delay-output 8000"
};
var fastCommand = new ProjectCommand
{
    Name = "Fast command",
    CommandText = $"\"{dotnetPath}\" \"{typeof(Program).Assembly.Location}\" --emit-unicode-output"
};
var concurrentProject = new ManagedProject
{
    Name = "Concurrent smoke test",
    WorkingDirectory = AppContext.BaseDirectory,
    Commands = new List<ProjectCommand> { slowCommand, fastCommand }
};
var fastExitCompletion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
concurrentManager.ProcessExited += (_, eventArgs) =>
{
    if (eventArgs.CommandId == fastCommand.Id)
    {
        fastExitCompletion.TrySetResult(eventArgs.ExitCode);
    }
};

await concurrentManager.StartAsync(concurrentProject, slowCommand);
await concurrentManager.StartAsync(concurrentProject, fastCommand);
var fastExitCode = await fastExitCompletion.Task.WaitAsync(TimeSpan.FromSeconds(10));
if (fastExitCode != 0 || !concurrentManager.IsRunning(slowCommand.Id))
{
    throw new InvalidOperationException("A slow command blocked another command from completing independently.");
}
await concurrentManager.DisposeAsync();

if (typeof(MainViewModel).Assembly.GetName().Name != "XProj" ||
    Path.GetFileName(typeof(MainViewModel).Assembly.Location) != "XProj.dll")
{
    throw new InvalidOperationException("The application assembly name must be XProj.");
}

var lifecycleDataStore = new JsonDataStore(Path.Combine(AppContext.BaseDirectory, "log-lifecycle-data"));
var lifecycleCommand = new ProjectCommand
{
    Name = "Lifecycle command",
    CommandText = $"\"{dotnetPath}\" \"{typeof(Program).Assembly.Location}\" --delay-output 8000"
};
var otherLifecycleCommand = new ProjectCommand { Name = "Other command", CommandText = "echo other" };
await lifecycleDataStore.SaveAsync(new AppData
{
    Projects = new List<ManagedProject>
    {
        new()
        {
            Name = "Log lifecycle project",
            WorkingDirectory = AppContext.BaseDirectory,
            Commands = new List<ProjectCommand> { lifecycleCommand, otherLifecycleCommand }
        }
    }
});
var lifecycleProcessManager = new ProcessManager();
var lifecycleViewModel = new MainViewModel(
    lifecycleDataStore,
    lifecycleProcessManager,
    new SystemLauncher());
await lifecycleViewModel.InitializeAsync();
var lifecycleRuntime = lifecycleViewModel.Commands.Single(item => item.Command.Id == lifecycleCommand.Id);
var otherLifecycleRuntime = lifecycleViewModel.Commands.Single(item => item.Command.Id == otherLifecycleCommand.Id);
string? latestReplacementText = null;
lifecycleViewModel.LogDisplayUpdated += (_, eventArgs) =>
{
    if (eventArgs.ReplacementText is not null)
    {
        latestReplacementText = eventArgs.ReplacementText;
    }
};

await lifecycleViewModel.RunCommandAsync(lifecycleRuntime);
await lifecycleViewModel.StopCommandAsync(lifecycleRuntime);
latestReplacementText = null;
lifecycleViewModel.SelectedCommand = otherLifecycleRuntime;
lifecycleViewModel.SelectedCommand = lifecycleRuntime;
if (latestReplacementText != string.Empty)
{
    throw new InvalidOperationException("A stopped command retained its log after switching away.");
}

latestReplacementText = null;
await lifecycleViewModel.RunCommandAsync(lifecycleRuntime);
if (latestReplacementText != string.Empty)
{
    throw new InvalidOperationException("Restarting a command did not begin with an empty log.");
}
await lifecycleViewModel.StopCommandAsync(lifecycleRuntime);
await lifecycleViewModel.ShutdownAsync();

const int BulkLineCount = 20_000;
var bulkManager = new ProcessManager();
var bulkOutput = new StringBuilder();
var bulkOutputEventCount = 0;
var bulkExitCompletion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
var bulkCommand = new ProjectCommand
{
    Name = "Bulk output",
    CommandText = $"\"{dotnetPath}\" \"{typeof(Program).Assembly.Location}\" --emit-bulk-output {BulkLineCount}"
};
var bulkProject = new ManagedProject
{
    Name = "Bulk output test",
    WorkingDirectory = AppContext.BaseDirectory,
    Commands = new List<ProjectCommand> { bulkCommand }
};
bulkManager.OutputReceived += (_, eventArgs) =>
{
    if (eventArgs.CommandId != bulkCommand.Id || !eventArgs.Text.Contains("bulk-", StringComparison.Ordinal))
    {
        return;
    }

    lock (bulkOutput)
    {
        bulkOutputEventCount++;
        bulkOutput.AppendLine(eventArgs.Text);
    }
};
bulkManager.ProcessExited += (_, eventArgs) =>
{
    if (eventArgs.CommandId == bulkCommand.Id)
    {
        bulkExitCompletion.TrySetResult(eventArgs.ExitCode);
    }
};

await bulkManager.StartAsync(bulkProject, bulkCommand);
var bulkExitCode = await bulkExitCompletion.Task.WaitAsync(TimeSpan.FromSeconds(20));
await bulkManager.DisposeAsync();
var capturedBulkLineCount = bulkOutput
    .ToString()
    .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
    .Count(line => line.StartsWith("bulk-", StringComparison.Ordinal));
if (bulkExitCode != 0 || capturedBulkLineCount != BulkLineCount)
{
    throw new InvalidOperationException(
        $"Bulk output capture failed. Exit: {bulkExitCode}; expected lines: {BulkLineCount}; actual: {capturedBulkLineCount}.");
}
if (bulkOutputEventCount >= BulkLineCount / 4)
{
    throw new InvalidOperationException(
        $"Bulk output was not batched effectively. Lines: {BulkLineCount}; events: {bulkOutputEventCount}.");
}

Console.WriteLine(
    $"Persistence, concurrent commands, rolling logs, and batched process output tests passed ({BulkLineCount} lines in {bulkOutputEventCount} events).");

internal sealed class StaticReleaseRedirectHandler(string releaseUrl) : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(CreateResponse());

    private HttpResponseMessage CreateResponse()
    {
        RequestCount++;
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, releaseUrl)
        };
    }
}
