using System.Diagnostics;

namespace XProj.Plugin.Wsl;

public sealed class WslCommandSession : IAsyncDisposable
{
    private readonly Process _process;

    internal WslCommandSession(Process process)
    {
        _process = process;
    }

    public Process Process => _process;

    public bool HasExited => _process.HasExited;

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (SystemException)
            {
            }
        }

        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
    }
}
