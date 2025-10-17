using System;
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
        JobManager.AddJob(() => oldValue?.Dispose(), s => s.ToRunOnceIn(4).Seconds());
    }

    private void OnSwitch()
    {
        if (Index == 0)
        {
            var framePlayer = new MediaFramePlayer();
            framePlayer.PlayOnThread(Path.Combine(Environment.CurrentDirectory, "1.mp4"));
            framePlayer.MediaPlayerStarted += (sender, args) =>
            {
                FramePlayer = framePlayer;
                Index = 1;
            };
        }
        else
        {
            var framePlayer = new MediaFramePlayer();
            framePlayer.PlayOnThread(Path.Combine(Environment.CurrentDirectory, "2.mp4"));
            framePlayer.MediaPlayerStarted += (sender, args) =>
            {
                FramePlayer = framePlayer;
                Index = 0;
            };
        }
    }
}