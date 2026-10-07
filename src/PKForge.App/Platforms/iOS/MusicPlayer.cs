using System.Text.Json;
using AVFoundation;
using Foundation;
using PKForge.Domain;

namespace PKForge.App.Platforms.iOS;

/// <summary>
/// Background music on iOS: the player's own audio files, copied to the cache when a track
/// starts (AVAudioPlayer keeps reading the file while it plays, after the picker's scope closed).
/// It mixes with other audio and steps aside when the app leaves the screen.
/// </summary>
public sealed class MusicPlayer : IMusicPlayer, IDisposable
{
    private const string LibraryKey = "music.library.ios";
    private const string OrderKey = "music.order";
    private const string AutostartKey = "music.autostart";
    private readonly List<MusicTrack> _library;
    private AVAudioPlayer? _player;
    private int? _index;
    private bool _pausedForBackground;
    private bool _autostarted;

    public MusicPlayer()
    {
        try { _library = JsonSerializer.Deserialize<List<MusicTrack>>(Preferences.Default.Get(LibraryKey, "[]")) ?? []; }
        catch (JsonException) { _library = []; }
    }

    public IReadOnlyList<MusicTrack> Library => _library;
    public bool IsPlaying => _player?.Playing == true;
    public int? CurrentIndex => _index;
    public string? LastError { get; private set; }

    public MusicOrder Order
    {
        get => (MusicOrder)Preferences.Default.Get(OrderKey, (int)MusicOrder.InOrder);
        private set => Preferences.Default.Set(OrderKey, (int)value);
    }

    public bool Autostart
    {
        get => Preferences.Default.Get(AutostartKey, false);
        private set => Preferences.Default.Set(AutostartKey, value);
    }

    public int Add(IReadOnlyList<PickedDocument> documents)
    {
        var added = 0;
        foreach (var document in documents)
        {
            if (_library.Any(track => track.DocumentId == document.DocumentId)) continue;
            _library.Add(new MusicTrack(document.DocumentId, Path.GetFileNameWithoutExtension(document.DisplayName)));
            added++;
        }
        Persist();
        return added;
    }

    public void Remove(int index)
    {
        if (index < 0 || index >= _library.Count) return;
        if (_index == index) StopInternal();
        _library.RemoveAt(index);
        Persist();
    }

    public void Clear()
    {
        StopInternal();
        _library.Clear();
        Persist();
    }

    public void Play()
    {
        if (_library.Count == 0) return;
        if (_player is not null && !_player.Playing)
        {
            _player.Play();
            return;
        }
        StartAt(_index ?? (Order == MusicOrder.Shuffle ? Random.Shared.Next(_library.Count) : 0));
    }

    public void Pause() => _player?.Pause();

    public void Skip()
    {
        if (_library.Count == 0) return;
        var next = Order == MusicOrder.Shuffle
            ? Random.Shared.Next(_library.Count)
            : ((_index ?? -1) + 1) % _library.Count;
        StartAt(next);
    }

    public void SetOrder(MusicOrder order) => Order = order;
    public void SetAutostart(bool on) => Autostart = on;

    public void MaybeAutostart()
    {
        if (_autostarted || !Autostart || _library.Count == 0) return;
        _autostarted = true;
        Play();
    }

    public void PauseForBackground()
    {
        if (_player?.Playing != true) return;
        _pausedForBackground = true;
        _player.Pause();
    }

    public void ResumeFromBackground()
    {
        if (!_pausedForBackground) return;
        _pausedForBackground = false;
        _player?.Play();
    }

    private void StartAt(int index)
    {
        StopInternal();
        if (index < 0 || index >= _library.Count) return;
        _index = index;
        var track = _library[index];
        try
        {
            var cache = Path.Combine(FileSystem.CacheDirectory, "music");
            Directory.CreateDirectory(cache);
            var extension = Path.GetExtension(IosFiles.Access(track.DocumentId, path => path));
            var copy = Path.Combine(cache, $"track{index}{extension}");
            IosFiles.Access(track.DocumentId, path => File.Copy(path, copy, overwrite: true));

            var session = AVAudioSession.SharedInstance();
            session.SetCategory(AVAudioSessionCategory.Ambient);
            session.SetActive(true);

            var player = AVAudioPlayer.FromUrl(NSUrl.FromFilename(copy), out var error);
            if (player is null)
            {
                LastError = error?.LocalizedDescription ?? "The track could not be opened.";
                return;
            }
            player.FinishedPlaying += (_, _) => MainThread.BeginInvokeOnMainThread(Skip);
            player.PrepareToPlay();
            player.Play();
            _player = player;
            LastError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            LastError = error.Message;
        }
    }

    private void StopInternal()
    {
        _player?.Stop();
        _player?.Dispose();
        _player = null;
        _pausedForBackground = false;
    }

    private void Persist() => Preferences.Default.Set(LibraryKey, JsonSerializer.Serialize(_library));

    public void Dispose() => StopInternal();
}
