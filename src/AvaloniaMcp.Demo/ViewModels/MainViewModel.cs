using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
