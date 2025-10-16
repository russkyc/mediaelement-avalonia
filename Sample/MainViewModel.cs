using System;
using Avalonia.Controls.Shapes;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentScheduler;
using Path = System.IO.Path;

namespace Sample;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private int _index;
    [ObservableProperty] private MediaFramePlayer? _framePlayer;

    public MainViewModel()
    {
        JobManager.AddJob(OnSwitch, s => s.ToRunNow().AndEvery(10).Seconds());
    }

    partial void OnFramePlayerChanged(MediaFramePlayer? oldValue, MediaFramePlayer? newValue)
    {
        JobManager.AddJob(() => oldValue?.Dispose(), s => s.ToRunOnceIn(5).Seconds());
    }

    private void OnSwitch()
    {
        if (Index == 0)
        {
            var framePlayer = new MediaFramePlayer();
            framePlayer.Play(Path.Combine(Environment.CurrentDirectory, "1.mp4"));
            FramePlayer = framePlayer;
            Index = 1;
        }
        else
        {
            var framePlayer = new MediaFramePlayer();
            framePlayer.Play(Path.Combine(Environment.CurrentDirectory, "2.mp4"));
            FramePlayer = framePlayer;
            Index = 0;
        }
    }
}