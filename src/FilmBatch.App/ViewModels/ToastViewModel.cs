using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.Core.Commands;

namespace FilmBatch.App.ViewModels;

/// <summary>非阻斷提示（計畫書 2.5 ToastBar）：3 秒後自動消失，可附「復原」按鈕。</summary>
public sealed partial class ToastViewModel(CommandStack stack) : ObservableObject
{
    private DispatcherTimer? _timer;

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private bool _canUndo;

    public void Show(string message, bool canUndo = false)
    {
        Message = message;
        CanUndo = canUndo;
        IsVisible = true;
        _timer ??= new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Normal, (_, _) => Hide());
        _timer.Stop();
        _timer.Start();
    }

    [RelayCommand]
    private void Undo()
    {
        stack.Undo();
        Hide();
    }

    [RelayCommand]
    private void Hide()
    {
        _timer?.Stop();
        IsVisible = false;
    }
}
