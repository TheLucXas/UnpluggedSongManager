using System;
using System.IO;
using System.Windows.Media;

namespace USM.Core.Services
{
    public class AudioPlayerService
    {
        private static AudioPlayerService? _instance;
        public static AudioPlayerService Instance => _instance ??= new AudioPlayerService();

        private readonly MediaPlayer _player = new();
        public string? CurrentSource { get; private set; }
        public bool IsPlaying { get; private set; }

        public event Action? PlaybackStarted;
        public event Action? PlaybackStopped;

        public AudioPlayerService()
        {
            _player.MediaEnded += (s, e) => Stop();
            _player.MediaFailed += (s, e) => Stop();
            _player.Volume = 0.85;
        }

        public void Play(string source)
        {
            Stop();

            try
            {
                CurrentSource = source;
                Uri uri;
                if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    uri = new Uri(source);
                }
                else
                {
                    if (!File.Exists(source)) return;
                    uri = new Uri(Path.GetFullPath(source));
                }

                _player.Open(uri);
                _player.Play();
                IsPlaying = true;
                PlaybackStarted?.Invoke();
            }
            catch
            {
                Stop();
            }
        }

        public void Stop()
        {
            try
            {
                _player.Stop();
                _player.Close();
            }
            catch { }

            IsPlaying = false;
            CurrentSource = null;
            PlaybackStopped?.Invoke();
        }

        public void SetVolume(double volume)
        {
            _player.Volume = Math.Clamp(volume, 0.0, 1.0);
        }
    }
}
