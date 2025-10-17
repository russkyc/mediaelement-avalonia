using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace Sample;

public sealed class MediaFramePlayer : INotifyPropertyChanged, IDisposable
{
    // Fields
    private readonly MediaPlayer _player;
    private readonly Lock _bufferLock = new();
    private WriteableBitmap? _currentFrame;
    private IntPtr _videoBuffer = IntPtr.Zero;
    private uint _videoHeight;
    private uint _videoPitch;
    private bool _isDisposed;
    private Thread? _playerThread;
    private string? _mediaPath;
    private ManualResetEventSlim _stopEvent = new();

    // Properties
    public WriteableBitmap? CurrentFrame
    {
        get => _currentFrame;
        private set
        {
            _currentFrame = value;
            OnPropertyChanged();
        }
    }

    // Events
    public event PropertyChangedEventHandler? PropertyChanged;

    // Constructor
    public MediaFramePlayer()
    {
        _player = new MediaPlayer(App.LibVlc);
        ConfigureVideoCallbacks();
    }

    ~MediaFramePlayer()
    {
        Dispose();
    }

    // Public Methods
    public void Play(string mediaPath, bool loop = false)
    {
        EnsureNotDisposed();
        var media = CreateMedia(mediaPath, loop);
        _player.Play(media);
    }

    public void Stop() => _player.Stop();

    public void Pause() => _player.Pause();

    public void PlayOnThread(string mediaPath, bool loop = false)
    {
        EnsureNotDisposed();
        _mediaPath = mediaPath;
        _stopEvent.Reset();
        _playerThread = new Thread(() => PlayerThreadProc(loop)) { IsBackground = true };
        _playerThread.Start();
    }

    public void StopThreaded()
    {
        _stopEvent.Set();
        _player.Stop();
        _playerThread?.Join();
    }

    // Private Methods
    private void ConfigureVideoCallbacks()
    {
        _player.SetVideoFormatCallbacks(VideoFormat, CleanupVideo);
        _player.SetVideoCallbacks(LockVideo, null, DisplayVideo);
    }

    private void PlayerThreadProc(bool loop)
    {
        if (_mediaPath is null) return;
        var media = CreateMedia(_mediaPath, loop);
        _player.Play(media);
        while (!_stopEvent.Wait(100))
        {
            if (_isDisposed) break;
        }
    }

    private Media CreateMedia(string mediaPath, bool loop)
    {
        return new Media(App.LibVlc, mediaPath, options: loop ? "input-repeat=65535" : string.Empty);
    }

    private uint VideoFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        SetVideoFormat(chroma, width, height, ref pitches, ref lines);
        AllocateVideoBuffer();
        CreateBitmap(width, height);
        return 1;
    }

    private void SetVideoFormat(IntPtr chroma, uint width, uint height, ref uint pitches, ref uint lines)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lines);
        ArgumentOutOfRangeException.ThrowIfNegative(pitches);
        Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);
        _videoHeight = height;
        _videoPitch = width * 4;
        pitches = _videoPitch;
        lines = height;
    }

    private void AllocateVideoBuffer()
    {
        lock (_bufferLock)
        {
            FreeVideoBuffer();
            _videoBuffer = Marshal.AllocHGlobal(GetBufferSize());
        }
    }

    private void CreateBitmap(uint width, uint height)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            CurrentFrame = new WriteableBitmap(
                new Avalonia.PixelSize((int)width, (int)height),
                new Avalonia.Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque);
        }).Wait();
    }

    private IntPtr LockVideo(IntPtr opaque, IntPtr planes)
    {
        lock (_bufferLock)
        {
            Marshal.WriteIntPtr(planes, _videoBuffer);
        }
        return IntPtr.Zero;
    }

    private void DisplayVideo(IntPtr opaque, IntPtr picture)
    {
        if (!IsVideoReady()) return;
        var frameData = CopyFrameData();
        UpdateFrameOnUiThread(frameData);
    }

    private void CleanupVideo(ref IntPtr opaque)
    {
        lock (_bufferLock)
        {
            FreeVideoBuffer();
        }
    }

    private bool IsVideoReady() => CurrentFrame != null && _videoBuffer != IntPtr.Zero;

    private IntPtr CopyFrameData()
    {
        lock (_bufferLock)
        {
            var bufferSize = GetBufferSize();
            var frameBuffer = Marshal.AllocHGlobal(bufferSize);
            unsafe
            {
                Buffer.MemoryCopy(
                    _videoBuffer.ToPointer(),
                    frameBuffer.ToPointer(),
                    bufferSize,
                    bufferSize);
            }
            return frameBuffer;
        }
    }

    private void UpdateFrameOnUiThread(IntPtr frameBuffer)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                CopyFrameToWriteableBitmap(frameBuffer);
                OnPropertyChanged(nameof(CurrentFrame));
            }
            finally
            {
                Marshal.FreeHGlobal(frameBuffer);
            }
        }, DispatcherPriority.Send);
    }

    private void CopyFrameToWriteableBitmap(IntPtr sourceBuffer)
    {
        var bufferSize = GetBufferSize();
        var width = (int)(_videoPitch / 4);
        var height = (int)_videoHeight;
        var bitmap = new WriteableBitmap(
            new Avalonia.PixelSize(width, height),
            new Avalonia.Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
        using var frameBuffer = bitmap.Lock();
        unsafe
        {
            Buffer.MemoryCopy(
                sourceBuffer.ToPointer(),
                frameBuffer.Address.ToPointer(),
                bufferSize,
                bufferSize);
        }
        CurrentFrame = bitmap;
    }

    private int GetBufferSize() => (int)(_videoPitch * _videoHeight);

    private void FreeVideoBuffer()
    {
        if (_videoBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_videoBuffer);
            _videoBuffer = IntPtr.Zero;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void EnsureNotDisposed()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(MediaFramePlayer));
    }

    // Disposal
    public void Dispose()
    {
        if (_isDisposed) return;
        StopThreaded();
        _player.Dispose();
        lock (_bufferLock)
        {
            FreeVideoBuffer();
        }
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
