using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Input;

namespace AvaloniaMcp.Demo.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private string _greeting = "Hello, Avalonia MCP!";
    private string _inputText = "";
    private int _clickCount;
    private string _statusMessage = "Ready";
    private bool _isToggled;
    private double _sliderValue = 50;
    private string _selectedItem = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Greeting
    {
        get => _greeting;
        set { _greeting = value; OnPropertyChanged(); }
    }

    public string InputText
    {
        get => _inputText;
        set { _inputText = value; OnPropertyChanged(); }
    }

    public int ClickCount
    {
        get => _clickCount;
        set { _clickCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(ClickCountText)); }
    }

    public string ClickCountText => $"Clicked {ClickCount} times";

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public bool IsToggled
    {
        get => _isToggled;
        set { _isToggled = value; OnPropertyChanged(); StatusMessage = $"Toggle is now {(value ? "ON" : "OFF")}"; }
    }

    public double SliderValue
    {
        get => _sliderValue;
        set { _sliderValue = value; OnPropertyChanged(); StatusMessage = $"Slider: {value:F0}%"; }
    }

    public string SelectedItem
    {
        get => _selectedItem;
        set { _selectedItem = value; OnPropertyChanged(); StatusMessage = $"Selected: {value}"; }
    }

    public ObservableCollection<string> Items { get; } =
    [
        "Apple",
        "Banana",
        "Cherry",
        "Date",
        "Elderberry",
    ];

    public ObservableCollection<TodoItem> Todos { get; } =
    [
        new("Buy groceries", true),
        new("Write unit tests", false),
        new("Review PR", false),
        new("Deploy to staging", false),
    ];

    public ICommand IncrementCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand AddItemCommand { get; }
    public ICommand CrashCommand { get; }
    public ICommand FreezeUiCommand { get; }
    public ICommand InfiniteLoopCommand { get; }
    public ICommand BackgroundHangCommand { get; }
    public ICommand DelayedCrashCommand { get; }

    public MainViewModel()
    {
        IncrementCommand = new RelayCommand(() =>
        {
            ClickCount++;
        });

        ResetCommand = new RelayCommand(() =>
        {
            ClickCount = 0;
            InputText = "";
            SliderValue = 50;
            IsToggled = false;
            StatusMessage = "Reset!";
        });

        AddItemCommand = new RelayCommand(() =>
        {
            if (!string.IsNullOrWhiteSpace(InputText))
            {
                Items.Add(InputText);
                Todos.Add(new TodoItem(InputText, false));
                InputText = "";
            }
        });

        CrashCommand = new RelayCommand(() =>
        {
            throw new InvalidOperationException("This is a test crash triggered by the Crash button!");
        });

        // Freezes the UI thread for 60 seconds via Thread.Sleep.
        // MCP calls should time out and report the UI thread as frozen.
        FreezeUiCommand = new RelayCommand(() =>
        {
            StatusMessage = "Freezing UI thread for 60s...";
            Thread.Sleep(60_000);
            StatusMessage = "UI thread unfroze!";
        });

        // Infinite busy loop on the UI thread — CPU pegged at 100%, UI completely stuck.
        // Only way to recover is to kill the process.
        InfiniteLoopCommand = new RelayCommand(() =>
        {
            StatusMessage = "Entering infinite loop...";
            long i = 0;
            while (true) { i++; } // intentional infinite loop for testing
        });

        // Starts a background task that blocks, then tries to update the UI.
        // The UI stays responsive but the StatusMessage shows "waiting..." forever.
        BackgroundHangCommand = new RelayCommand(() =>
        {
            StatusMessage = "Background task started (will hang)...";
            Task.Run(() =>
            {
                // Simulate a background operation that hangs (e.g. deadlocked HTTP call)
                using var cts = new CancellationTokenSource();
                try { Task.Delay(Timeout.Infinite, cts.Token).Wait(); }
                catch { }
            });
        });

        // Crashes the app after a short delay — tests the crash file detection.
        // Uses a raw Thread (not Task.Run) so the unhandled exception triggers
        // AppDomain.CurrentDomain.UnhandledException → WriteCrashFile before process death.
        DelayedCrashCommand = new RelayCommand(() =>
        {
            StatusMessage = "App will crash in 3 seconds...";
            var crashThread = new Thread(() =>
            {
                Thread.Sleep(3000);
                throw new InvalidOperationException(
                    "Delayed crash! This simulates an unhandled background exception. " +
                    "Stack trace and exception details should appear in the MCP crash report.");
            })
            { IsBackground = true, Name = "MCP-CrashTest" };
            crashThread.Start();
        });
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class TodoItem : INotifyPropertyChanged
{
    private string _title;
    private bool _isDone;

    public string Title
    {
        get => _title;
        set { _title = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title))); }
    }

    public bool IsDone
    {
        get => _isDone;
        set { _isDone = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDone))); }
    }

    public TodoItem(string title, bool isDone)
    {
        _title = title;
        _isDone = isDone;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
