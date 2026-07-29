using System.Text;
using ProjectManager.Wpf.Infrastructure;
using ProjectManager.Wpf.Models;

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

var persistenceDirectory = Path.Combine(AppContext.BaseDirectory, "smoke-data");
var dataStore = new JsonDataStore(persistenceDirectory);
var parentGroup = new ProjectGroup { Name = "Parent" };
var childGroup = new ProjectGroup { Name = "Child", ParentId = parentGroup.Id };
var persistedData = new AppData
{
    Settings = new AppSettings
    {
        Theme = "Light",
        CloseBehavior = "Exit",
        UiFontFamily = "Microsoft YaHei UI",
        UiFontSize = 15,
        LogFontFamily = "Consolas",
        LogFontSize = 14,
        LogFontBold = true,
        LogFontItalic = true
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

var loadedData = await dataStore.LoadAsync();
if (loadedData.Groups.Count != 2 ||
    loadedData.Projects.Single().GroupId != childGroup.Id ||
    loadedData.Settings.Theme != "Light" ||
    loadedData.Settings.CloseBehavior != "Exit" ||
    loadedData.Settings.UiFontFamily != "Microsoft YaHei UI" ||
    loadedData.Settings.UiFontSize != 15 ||
    loadedData.Settings.LogFontFamily != "Consolas" ||
    loadedData.Settings.LogFontSize != 14 ||
    !loadedData.Settings.LogFontBold ||
    !loadedData.Settings.LogFontItalic)
{
    throw new InvalidOperationException("Nested group or log font persistence smoke test failed.");
}

var exportPath = Path.Combine(persistenceDirectory, "exported-config.json");
await dataStore.ExportAsync(loadedData, exportPath);
var importedData = await dataStore.ImportAsync(exportPath);
if (importedData.Settings.Theme != "Light" ||
    importedData.Settings.CloseBehavior != "Exit" ||
    importedData.Settings.UiFontFamily != "Microsoft YaHei UI" ||
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

Console.WriteLine("Persistence, concurrent commands, cmd shell, and process output smoke tests passed.");
