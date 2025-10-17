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

        public event EventHandler<TimeSpan>? OnPlay;
        public event EventHandler? OnEnd;

        public void ClearPlayingHandlers()
        {
            OnPlay = null;
            OnEnd = null;
        }

        private void OnPlaying(TimeSpan timestamp)
        {
            var trimmedTimestamp = new TimeSpan(timestamp.Hours, timestamp.Minutes, timestamp.Seconds);
            CurrentTimestamp = timestamp; // Trim milliseconds, only hours, minutes, seconds
            OnPlay?.Invoke(this, trimmedTimestamp);
            if (!timestamp.Equals(Duration) || _loop) return;
            OnEnd?.Invoke(this, EventArgs.Empty);
            Pause();
        }

        private TimeSpan _duration;
        private TimeSpan _currentTimestamp;

        public TimeSpan Duration
        {
            get => _duration;
            set
            {
                _duration = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CurrentPercent));
            }
        }

        public TimeSpan CurrentTimestamp
        {
            get => _currentTimestamp;
            set
            {
                _currentTimestamp = TimeSpan.FromSeconds(Math.Clamp(value.TotalSeconds, 0, Duration.TotalSeconds));
                OnPropertyChanged();
                OnPropertyChanged(nameof(CurrentPercent));
            }
        }

        public double CurrentPercent => Duration.TotalSeconds > 0 ? (CurrentTimestamp.TotalSeconds / Duration.TotalSeconds) * 100 : 0;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler? MediaPlayerStarted;

        public MediaFramePlayer(LibVLC libVlc)
        {
            _libVlc = libVlc;
            _player = new MediaPlayer(_libVlc);
            _player.TimeChanged += (_, args) => OnPlaying(TimeSpan.FromMilliseconds(args.Time));
            ConfigureVideoCallbacks();
        }

        ~MediaFramePlayer()
        {
            Dispose();
        }

        public void Load(string mediaPath, bool loop = false)
        {
            if (IsPlaying || _isDisposed) return;
            InitializeMedia(mediaPath, loop);
        }

        public void Play(string mediaPath, bool loop = false)
        {
            if (IsPlaying || _isDisposed) return;
            InitializeMedia(mediaPath, loop);
        }

        public void Play()
        {
            if (IsPlaying || _isDisposed || _mediaPath is null) return;
            _player.Pause();
            IsPlaying = true;
            OnPropertyChanged(nameof(IsPlaying));
        }

        private void InitializeMedia(string mediaPath, bool loop)
        {
            _loop = loop;
            _mediaPath = mediaPath;
            var media = new Media(_libVlc, _mediaPath, options: "input-repeat=65535");

            media.ParsedChanged += (sender, args) =>
            {
                if (args.ParsedStatus == MediaParsedStatus.Done)
                {
                    Duration = TimeSpan.FromMilliseconds(media.Duration);
                }
            };

            media.Parse(); // Trigger metadata parsing
            _player.Play(media);
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
            IsPlaying = false;
            _player.Pause();
            _player.SeekTo(TimeSpan.Zero);
            OnPlaying(TimeSpan.Zero);
            OnPropertyChanged(nameof(IsPlaying));
        }

        public void Pause()
        {
            if (!IsPlaying) return;
            IsPlaying = false;
            _player.Pause();
            OnPropertyChanged(nameof(IsPlaying));
        }

        public void Seek(TimeSpan position)
        {
            if (_isDisposed) return;
            if (_player.Time == 0) return;
            try
            {
                var clampedPosition = TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, Duration.TotalSeconds));
                _player.Time = (long)clampedPosition.TotalMilliseconds;
                OnPlaying(clampedPosition);
            }
            catch (Exception)
            {
                // Ignored
            }
        }

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

        private void ManageVideoBuffer(Action bufferAction)
        {
            lock (_bufferLock)
            {
                bufferAction();
            }
        }

        private void AllocateVideoBuffer()
        {
            ManageVideoBuffer(() =>
            {
                FreeVideoBuffer();
                _videoBuffer = Marshal.AllocHGlobal(GetBufferSize());
            });
        }

        private void FreeVideoBuffer()
        {
            ManageVideoBuffer(() =>
            {
                if (_videoBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_videoBuffer);
                    _videoBuffer = IntPtr.Zero;
                }
            });
        }

        private void CreateBitmap(uint width, uint height)
        {
            // Ensure the bitmap matches the source video resolution
            CurrentFrame = new WriteableBitmap(
                new Avalonia.PixelSize((int)width, (int)height),
                new Avalonia.Vector(96, 96), // Dpi, can be adjusted as needed
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
                }, DispatcherPriority.Render);
            }
        }

        private void ManageReusableFrameBuffer(Action bufferAction)
        {
            lock (_frameBufferLock)
            {
                bufferAction();
            }
        }

        private void FreeReusableFrameBuffer()
        {
            ManageReusableFrameBuffer(() =>
            {
                if (_reusableFrameBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_reusableFrameBuffer);
                    _reusableFrameBuffer = IntPtr.Zero;
                }
            });
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

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

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

