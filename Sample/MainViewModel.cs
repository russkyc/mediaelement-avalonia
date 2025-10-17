using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentScheduler;
using Russkyc.MediaElement;
using Path = System.IO.Path;

namespace Sample;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private int _index;
    [ObservableProperty] private MediaFramePlayer? _framePlayer;

    public MainViewModel()
    {
        JobManager.AddJob(OnSwitch, s => s.ToRunEvery(10).Seconds());
    }

    partial void OnFramePlayerChanged(MediaFramePlayer? oldValue, MediaFramePlayer? newValue)
    {
        JobManager.AddJob(() => oldValue?.Dispose(), s => s.ToRunOnceIn(4).Seconds());
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (FramePlayer is null) return;
        if (FramePlayer.IsPlaying)
        {
            FramePlayer?.Pause();
        }
        else
        {
            FramePlayer?.Play();
        }
    }

    [RelayCommand]
    private void SeekAdd10()
    {
        FramePlayer?.Seek(FramePlayer.CurrentTimestamp + TimeSpan.FromSeconds(10));
    }
    [RelayCommand]
    private void SeekLess10()
    {
        FramePlayer?.Seek(FramePlayer.CurrentTimestamp - TimeSpan.FromSeconds(10));
    }

    [RelayCommand]
    private void Stop()
    {
        FramePlayer?.Stop();
    }

    private void OnSwitch()
    {
        if (Index == 0)
        {
            Task.Run(() =>
            {
                var framePlayer = new MediaFramePlayer(App.LibVlc);
                framePlayer.Play(Path.Combine(Environment.CurrentDirectory, "1.mp4"));
                framePlayer.MediaPlayerStarted += (_, _) =>
                {
                    Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        FramePlayer = framePlayer;
                        Index = 1;
                    });
                };
            });
        }
        else
        {
            Task.Run(() =>
            {
                var framePlayer = new MediaFramePlayer(App.LibVlc);
                framePlayer.Play(Path.Combine(Environment.CurrentDirectory, "2.mp4"));
                framePlayer.MediaPlayerStarted += (_, _) =>
                {
                    Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        FramePlayer = framePlayer;
                        Index = 0;
                    });
                };
            });
        }
    }
}