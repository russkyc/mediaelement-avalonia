using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace Russkyc.MediaElement
{
    public sealed class MediaFramePlayer : INotifyPropertyChanged, IDisposable
    {
        // Fields
        private readonly LibVLC _libVlc;
        private readonly MediaPlayer _player;
        private readonly object _bufferLock = new object();
        private string? _mediaPath;
        private bool _loop;
        private WriteableBitmap? _currentFrame;
        private IntPtr _videoBuffer = IntPtr.Zero;
        private uint _videoHeight;
        private uint _videoPitch;
        private bool _isDisposed;
        private IntPtr _reusableFrameBuffer;
        private readonly object _frameBufferLock = new object();
        private bool _isUpdatingFrame;
        public bool IsPlaying { get; set; }

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
        public event EventHandler<TimeSpan>? OnPlay;

        public void ClearPlayingHandlers()
        {
            OnPlay = null;
        }

        // Method to trigger the Playing event with trimmed milliseconds
        private void OnPlaying(TimeSpan timestamp)
        {
            var trimmedTimestamp = new TimeSpan(timestamp.Hours, timestamp.Minutes, timestamp.Seconds);
            OnPropertyChanged(nameof(CurrentTimestamp));
            OnPlay?.Invoke(this, trimmedTimestamp);
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
        public MediaFramePlayer(LibVLC libVlc)
        {
            _libVlc = libVlc;
            _player = new MediaPlayer(_libVlc);
            _player.TimeChanged += (_, args) => { OnPlaying(TimeSpan.FromMilliseconds(args.Time)); };
            ConfigureVideoCallbacks();
        }

        ~MediaFramePlayer()
        {
            Dispose();
        }

        // Public Methods
        public void Load(string mediaPath, bool loop = false)
        {
            if (IsPlaying) return;
            if (_isDisposed) return;
            _loop = loop;
            _mediaPath = mediaPath;
            var media = new Media(_libVlc, _mediaPath, options: _loop ? "input-repeat=65535" : string.Empty);
            _player.Play(media);
            IsPlaying = true;
            OnPropertyChanged(nameof(IsPlaying));
        }
        // Public Methods
        public void Play(string mediaPath, bool loop = false)
        {
            if (IsPlaying) return;
            if (_isDisposed) return;
            _loop = loop;
            _mediaPath = mediaPath;
            var media = new Media(_libVlc, _mediaPath, options: _loop ? "input-repeat=65535" : string.Empty);
            _player.Play(media);
            IsPlaying = true;
            OnPropertyChanged(nameof(IsPlaying));
        }
        public void Play(bool loop = false)
        {
            if (IsPlaying) return;
            if (_isDisposed) return;
            if (_mediaPath is null) return;
            _loop = loop;
            _player.Pause();
            IsPlaying = true;
            OnPropertyChanged(nameof(IsPlaying));
        }

        private void OnMediaPlayerStarted()
        {
            if (CurrentFrame != null)
            {
                MediaPlayerStarted?.Invoke(this, EventArgs.Empty);
                MediaPlayerStarted = null;
            }
        }

        public void Stop()
        {
            if (!IsPlaying) return;
            _player.Pause();
            _player.SeekTo(TimeSpan.Zero);
            IsPlaying = false;
            OnPropertyChanged(nameof(IsPlaying));
        }

        public void Pause()
        {
            if (!IsPlaying) return;
            _player.Pause();
            IsPlaying = false;
            OnPropertyChanged(nameof(IsPlaying));
        }

        public void Seek(TimeSpan position)
        {
            if (_isDisposed) return;
            if (_player.Time == 0) return;
            try
            {
                _player.Time = (long)position.TotalMilliseconds;
            }
            catch (Exception)
            {
                // Ignored
            }
        }

        // Private Methods
        private void ConfigureVideoCallbacks()
        {
            _player.SetVideoFormatCallbacks(VideoFormat, CleanupVideo);
            _player.SetVideoCallbacks(LockVideo, null, DisplayVideo);
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
            Marshal.Copy(System.Text.Encoding.ASCII.GetBytes("RV32"), 0, chroma, 4);
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
            // Ensure the bitmap matches the source video resolution
            CurrentFrame = new WriteableBitmap(
                new Avalonia.PixelSize((int)width, (int)height),
                new Avalonia.Vector(96, 96), // DPI settings
                PixelFormat.Bgra8888, // High-quality pixel format
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
                }, DispatcherPriority.Render); // Use Render priority for smoother updates
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

            // Ensure the bitmap is created with the correct resolution and format
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
            if (_videoBuffer == IntPtr.Zero) return;
            Marshal.FreeHGlobal(_videoBuffer);
            _videoBuffer = IntPtr.Zero;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // Disposal
        public void Dispose()
        {
            if (_isDisposed) return;
            Stop();
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
}