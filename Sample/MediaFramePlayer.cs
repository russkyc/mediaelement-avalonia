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
    #region Fields
    
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
    
    #endregion

    #region Properties
    
    public WriteableBitmap? CurrentFrame
    {
        get => _currentFrame;
        private set
        {
            _currentFrame = value;
            OnPropertyChanged();
        }
    }
    
    #endregion

    #region Events
    
    public event PropertyChangedEventHandler? PropertyChanged;
    
    #endregion

    #region Initialization
    
    public MediaFramePlayer()
    {
        _player = new MediaPlayer(App.LibVlc);
        
        ConfigureVideoCallbacks();
    }

    private void ConfigureVideoCallbacks()
    {
        _player.SetVideoFormatCallbacks(VideoFormat, CleanupVideo);
        _player.SetVideoCallbacks(LockVideo, null, DisplayVideo);
    }
    
    #endregion

    #region Public Methods
    
    public void Play(string mediaPath, bool loop = false)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(MediaFramePlayer));
        
        var media = new Media(App.LibVlc, mediaPath, options: loop ? "input-repeat=65535" : string.Empty);
        _player.Play(media);
    }

    public void Stop()
    {
        _player.Stop();
    }

    public void Pause()
    {
        _player.Pause();
    }

    public void PlayOnThread(string mediaPath, bool loop = false)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(MediaFramePlayer));
        _mediaPath = mediaPath;
        _stopEvent.Reset();
        _playerThread = new Thread(() => PlayerThreadProc(loop))
        {
            IsBackground = true
        };
        _playerThread.Start();
    }
    
    private void PlayerThreadProc(bool loop = false)
    {
        var media = new Media(App.LibVlc, _mediaPath, options: loop ? "input-repeat=65535" : string.Empty);
        _player.Play(media);
        while (!_stopEvent.Wait(100))
        {
            // Thread stays alive while playing
            if (_isDisposed) break;
        }
    }

    public void StopThreaded()
    {
        _stopEvent.Set();
        _player.Stop();
        if (_playerThread != null && _playerThread.IsAlive)
            _playerThread.Join();
    }
    
    #endregion

    #region Video Callbacks
    
    private uint VideoFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, 
        ref uint pitches, ref uint lines)
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
        // Use RV32 (BGRA) format - native to Avalonia for optimal quality
        Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);
        
        _videoHeight = height;
        _videoPitch = width * 4; // 4 bytes per pixel (BGRA)
        
        pitches = _videoPitch;
        lines = height;
    }

    private void AllocateVideoBuffer()
    {
        var bufferSize = GetBufferSize();
        lock (_bufferLock)
        {
            FreeVideoBuffer();
            _videoBuffer = Marshal.AllocHGlobal(bufferSize);
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
    
    #endregion

    #region Frame Processing
    
    private bool IsVideoReady()
    {
        return CurrentFrame != null && _videoBuffer != IntPtr.Zero;
    }

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
    
    #endregion

    #region Helper Methods
    
    private int GetBufferSize()
    {
        return (int)(_videoPitch * _videoHeight);
    }

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
    
    #endregion

    #region Disposal
    
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
    
    #endregion
}
