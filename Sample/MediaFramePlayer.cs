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
    private readonly ManualResetEventSlim _stopEvent = new();
    private IntPtr _reusableFrameBuffer;
    private readonly Lock _frameBufferLock = new();
    private bool _isUpdatingFrame;

    // Properties
    public WriteableBitmap? CurrentFrame
    {
        get => _currentFrame;
        private set
        {
            _currentFrame = value;
            OnPropertyChanged();
            OnMediaPlayerStarted();
        }
    }

    // New event for timestamp updates
    public event EventHandler<TimeSpan>? Playing;

    public void ClearPlayingHandlers()
    {
        Playing = null;
    }

    // Method to trigger the Playing event with trimmed milliseconds
    private void OnPlaying(TimeSpan timestamp)
    {
        var trimmedTimestamp = new TimeSpan(timestamp.Hours, timestamp.Minutes, timestamp.Seconds);
        OnPropertyChanged(nameof(CurrentTimestamp));
        Playing?.Invoke(this, trimmedTimestamp);
    }

    public TimeSpan CurrentTimestamp
    {
        get
        {
            var timestamp = TimeSpan.FromMilliseconds(_player.Time);
            return new TimeSpan(timestamp.Hours, timestamp.Minutes, timestamp.Seconds);
        }
        set
        {
            _player.Time = (long)value.TotalMilliseconds;
            OnPlaying(TimeSpan.FromMilliseconds(_player.Time));
        }
    }

    // Events
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? MediaPlayerStarted;

    // Constructor
    public MediaFramePlayer()
    {
        _player = new MediaPlayer(App.LibVlc);
        _player.TimeChanged += (sender, args) => { OnPlaying(TimeSpan.FromMilliseconds(args.Time)); };
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

    private void OnMediaPlayerStarted()
    {
        if (CurrentFrame != null)
        {
            MediaPlayerStarted?.Invoke(this, EventArgs.Empty);
            MediaPlayerStarted = null;
        }
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

    public void Seek(TimeSpan position)
    {
        EnsureNotDisposed();
        _player.Time = (long)position.TotalMilliseconds;
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

    private uint VideoFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches,
        ref uint lines)
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
        CurrentFrame = new WriteableBitmap(
            new Avalonia.PixelSize((int)width, (int)height),
            new Avalonia.Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
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

        lock (_frameBufferLock)
        {
            if (_reusableFrameBuffer == IntPtr.Zero)
            {
                _reusableFrameBuffer = Marshal.AllocHGlobal(GetBufferSize());
            }

            unsafe
            {
                Buffer.MemoryCopy(
                    _videoBuffer.ToPointer(),
                    _reusableFrameBuffer.ToPointer(),
                    GetBufferSize(),
                    GetBufferSize());
            }
        }

        if (!_isUpdatingFrame)
        {
            _isUpdatingFrame = true;
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    lock (_frameBufferLock)
                    {
                        CopyFrameToWriteableBitmap(_reusableFrameBuffer);
                    }

                    OnPropertyChanged(nameof(CurrentFrame));
                }
                finally
                {
                    _isUpdatingFrame = false;
                }
            }, DispatcherPriority.Send);
        }
    }

    private void FreeReusableFrameBuffer()
    {
        lock (_frameBufferLock)
        {
            if (_reusableFrameBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_reusableFrameBuffer);
                _reusableFrameBuffer = IntPtr.Zero;
            }
        }
    }

    private void CleanupVideo(ref IntPtr opaque)
    {
        lock (_bufferLock)
        {
            FreeVideoBuffer();
        }

        FreeReusableFrameBuffer();
    }

    private bool IsVideoReady() => CurrentFrame != null && _videoBuffer != IntPtr.Zero;

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
        ClearPlayingHandlers();
        _player.Dispose();
        lock (_bufferLock)
        {
            FreeVideoBuffer();
        }

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}