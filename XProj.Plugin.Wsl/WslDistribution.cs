using System.ComponentModel;

namespace XProj.Plugin.Wsl;

public sealed class WslDistribution : INotifyPropertyChanged
{
    private bool _isRunning;

    public WslDistribution(string name, int version, bool isDefault, bool isRunning)
    {
        Name = name;
        Version = version;
        IsDefault = isDefault;
        _isRunning = isRunning;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }
    public int Version { get; }
    public bool IsDefault { get; }
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value)
            {
                return;
            }

            _isRunning = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        }
    }

    public string StatusText => IsRunning ? "运行中" : "已停止";
}
