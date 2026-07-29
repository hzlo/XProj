using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ProjectManager.Wpf.Models;

namespace ProjectManager.Wpf.Infrastructure;

public sealed class ProcessManager : IAsyncDisposable
{
    private static readonly Encoding StrictUtf8Encoding = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly Encoding ShellOutputEncoding = CreateShellOutputEncoding();
    private readonly ConcurrentDictionary<Guid, RunningProcess> _runningProcesses = new();

    public event EventHandler<ProcessOutputEventArgs>? OutputReceived;
    public event EventHandler<ProcessExitedEventArgs>? ProcessExited;

    public bool IsRunning(Guid commandId) => _runningProcesses.ContainsKey(commandId);

    public bool HasRunningCommands(Guid projectId) =>
        _runningProcesses.Values.Any(item => item.ProjectId == projectId);

    public async Task StartAsync(ManagedProject project, ProjectCommand command)
    {
        if (!Directory.Exists(project.WorkingDirectory))
        {
            throw new DirectoryNotFoundException($"工作目录不存在：{project.WorkingDirectory}");
        }

        if (string.IsNullOrWhiteSpace(command.CommandText))
        {
            throw new InvalidOperationException("命令不能为空。");
        }

        if (_runningProcesses.ContainsKey(command.Id))
        {
            throw new InvalidOperationException("该命令已经在运行。");
        }

        var shellPath = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var startInfo = new ProcessStartInfo
        {
            FileName = shellPath,
            Arguments = $"/D /S /C \"{command.CommandText}\"",
            WorkingDirectory = project.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var process = new Process
        {
            StartInfo = startInfo
        };
        var runningProcess = new RunningProcess(
            project.Id,
            command.Id,
            process,
            new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously));

        if (!_runningProcesses.TryAdd(command.Id, runningProcess))
        {
            process.Dispose();
            throw new InvalidOperationException("该命令已经在运行。");
        }

        try
        {
            var started = await Task.Run(process.Start).ConfigureAwait(false);
            if (!started)
            {
                throw new InvalidOperationException("无法启动命令进程。");
            }

            var standardOutputTask = ReadOutputAsync(process.StandardOutput.BaseStream, command.Id, false);
            var standardErrorTask = ReadOutputAsync(process.StandardError.BaseStream, command.Id, true);
            process.Exited += async (_, _) => await HandleProcessExitAsync(
                runningProcess,
                standardOutputTask,
                standardErrorTask);
            process.EnableRaisingEvents = true;
            OutputReceived?.Invoke(this, new ProcessOutputEventArgs(
                command.Id,
                $"> {command.CommandText}{Environment.NewLine}[Shell] {shellPath}{Environment.NewLine}[目录] {project.WorkingDirectory}",
                false));
            if (process.HasExited)
            {
                await HandleProcessExitAsync(runningProcess, standardOutputTask, standardErrorTask);
            }
        }
        catch
        {
            _runningProcesses.TryRemove(command.Id, out _);
            process.Dispose();
            throw;
        }
    }

    public async Task StopAsync(Guid commandId)
    {
        if (!_runningProcesses.TryGetValue(commandId, out var runningProcess))
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                if (!runningProcess.Process.HasExited)
                {
                    runningProcess.Process.Kill(entireProcessTree: true);
                }
            }).ConfigureAwait(false);

            await runningProcess.Completion.Task.ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
    }

    public async Task StopProjectAsync(Guid projectId)
    {
        var commandIds = _runningProcesses.Values
            .Where(item => item.ProjectId == projectId)
            .Select(item => item.CommandId)
            .ToArray();

        await Task.WhenAll(commandIds.Select(StopAsync));
    }

    public async Task StopAllAsync()
    {
        await Task.WhenAll(_runningProcesses.Keys.ToArray().Select(StopAsync));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync();
    }

    private static Encoding CreateShellOutputEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(
            CultureInfo.CurrentCulture.TextInfo.OEMCodePage,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ReplacementFallback);
    }

    private async Task ReadOutputAsync(Stream stream, Guid commandId, bool isError)
    {
        var buffer = new byte[16 * 1024];
        var line = new List<byte>();

        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            var outputBatch = new StringBuilder(bytesRead);
            var completedLineCount = 0;
            for (var index = 0; index < bytesRead; index++)
            {
                if (buffer[index] == (byte)'\n')
                {
                    AppendDecodedLine(outputBatch, line, completedLineCount > 0);
                    completedLineCount++;
                    line.Clear();
                    continue;
                }

                line.Add(buffer[index]);
            }

            if (completedLineCount > 0)
            {
                PublishOutput(commandId, outputBatch.ToString(), isError);
            }
        }

        if (line.Count > 0)
        {
            PublishOutput(commandId, DecodeOutput(line), isError);
        }
    }

    private static void AppendDecodedLine(StringBuilder outputBatch, List<byte> line, bool prependNewLine)
    {
        if (line.Count > 0 && line[^1] == (byte)'\r')
        {
            line.RemoveAt(line.Count - 1);
        }

        if (prependNewLine)
        {
            outputBatch.AppendLine();
        }

        outputBatch.Append(DecodeOutput(line));
    }

    private void PublishOutput(Guid commandId, string text, bool isError)
    {
        OutputReceived?.Invoke(this, new ProcessOutputEventArgs(commandId, text, isError));
    }

    private static string DecodeOutput(List<byte> bytes)
    {
        var byteSpan = CollectionsMarshal.AsSpan(bytes);
        try
        {
            return StrictUtf8Encoding.GetString(byteSpan);
        }
        catch (DecoderFallbackException)
        {
            return ShellOutputEncoding.GetString(byteSpan);
        }
    }

    private async Task HandleProcessExitAsync(
        RunningProcess runningProcess,
        Task standardOutputTask,
        Task standardErrorTask)
    {
        if (!_runningProcesses.TryRemove(runningProcess.CommandId, out _))
        {
            return;
        }

        var exitCode = 0;
        try
        {
            runningProcess.Process.WaitForExit();
            await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            exitCode = runningProcess.Process.ExitCode;
        }
        catch (InvalidOperationException)
        {
        }

        runningProcess.Completion.TrySetResult(exitCode);
        ProcessExited?.Invoke(this, new ProcessExitedEventArgs(runningProcess.CommandId, exitCode));
        runningProcess.Process.Dispose();
    }

    private sealed record RunningProcess(
        Guid ProjectId,
        Guid CommandId,
        Process Process,
        TaskCompletionSource<int> Completion);

}

public sealed record ProcessOutputEventArgs(Guid CommandId, string Text, bool IsError);
public sealed record ProcessExitedEventArgs(Guid CommandId, int ExitCode);
