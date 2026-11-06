using QuantumHub.Plugin.Music;
using Xunit;

namespace QuantumHub.Tests;

/// <summary>Логика музыки. Всё на подделках: настоящая громкость здесь не трогается.</summary>
public class MusicTests
{
    private const string YouTubeMusic = "com.github.th-ch.youtube-music";

    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Apps = ["YouTube Music", "Spotify"];

    private static MediaSession Session(string title = "Song", PlaybackState state = PlaybackState.Playing, string app = YouTubeMusic,
        double position = 30, double duration = 180, string artist = "Artist") =>
        new(app, title, artist, "Album", state, position, duration, Now);

    private readonly MusicTracker _tracker = new();

    private MusicChange Update(params MediaSession[] sessions) => _tracker.Update(sessions, Apps, Now);

    [Theory]
    [InlineData("com.github.th-ch.youtube-music", "YouTube Music")]
    [InlineData("YouTube Music", "youtube music")]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "spotify")]
    public void Names_match_whatever_the_separators(string name, string pattern) => Assert.True(AppNames.Matches(name, pattern));

    [Fact]
    public void An_empty_pattern_matches_nothing() => Assert.False(AppNames.Matches("anything", " - "));

    [Fact]
    public void Starting_to_play_is_an_event() => Assert.Equal(MusicChange.Started, Update(Session()));

    [Fact]
    public void A_player_found_already_paused_is_silent() => Assert.Equal(MusicChange.None, Update(Session(state: PlaybackState.Paused)));

    [Fact]
    public void Reports_track_change_pause_and_resume()
    {
        Update(Session("One"));

        Assert.Equal(MusicChange.TrackChanged, Update(Session("Two")));
        Assert.Equal(MusicChange.Paused, Update(Session("Two", PlaybackState.Paused)));
        Assert.Equal(MusicChange.Resumed, Update(Session("Two")));
    }

    [Fact]
    public void The_same_state_is_not_an_event()
    {
        Update(Session());

        Assert.Equal(MusicChange.None, Update(Session(position: 60)));
    }

    [Fact]
    public void A_browser_cannot_take_over_the_track()
    {
        Update(Session("Song"));

        var change = Update(Session("Song", PlaybackState.Paused), Session("Funny video", app: "chrome"));

        Assert.Equal(MusicChange.Paused, change);
        Assert.Equal("Song", _tracker.Current!.Title);
    }

    [Fact]
    public void Other_apps_alone_are_ignored()
    {
        Assert.Equal(MusicChange.None, Update(Session("Funny video", app: "chrome")));
        Assert.Null(_tracker.Current);
    }

    [Fact]
    public void A_playing_app_beats_a_paused_one_with_higher_priority()
    {
        Update(Session("Paused", PlaybackState.Paused), Session("Playing", app: "Spotify.exe"));

        Assert.Equal("Playing", _tracker.Current!.Title);
    }

    [Fact]
    public void A_pause_at_the_end_of_a_track_is_not_the_user_pausing()
    {
        Update(Session(position: 179));

        Assert.Equal(MusicChange.None, Update(Session(state: PlaybackState.Paused, position: 179.5)));
    }

    [Fact]
    public void An_empty_title_between_tracks_changes_nothing()
    {
        Update(Session("One"));

        Assert.Equal(MusicChange.None, Update(Session("")));
        Assert.Equal("One", _tracker.Current!.Title);
    }

    [Fact]
    public void A_closed_player_is_reported_once()
    {
        Update(Session());

        Assert.Equal(MusicChange.Stopped, Update());
        Assert.Equal(MusicChange.None, Update());
    }

    [Fact]
    public void Position_keeps_moving_while_playing_and_stops_at_the_end()
    {
        var session = Session(position: 30, duration: 40);

        Assert.Equal(35, session.PositionAt(Now.AddSeconds(5)));
        Assert.Equal(40, session.PositionAt(Now.AddSeconds(500)));
        Assert.Equal(30, (session with { State = PlaybackState.Paused }).PositionAt(Now.AddSeconds(5)));
    }

    private sealed class FakeAudio : IAudioSessions
    {
        public List<AudioSession> Sessions { get; } = [new("YouTube Music", 1, 0.2)];
        public List<(string Process, double Volume)> Changes { get; } = [];

        public Task<IReadOnlyList<AudioSession>> ListAsync() => Task.FromResult<IReadOnlyList<AudioSession>>(Sessions.ToList());

        public Task SetVolumeAsync(string process, double volume, int fadeMs)
        {
            Changes.Add((process, volume));
            var index = Sessions.FindIndex(session => session.Process == process);
            if (index >= 0) Sessions[index] = Sessions[index] with { Volume = volume };
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMemory : IDuckMemory
    {
        public string? Process { get; set; }
        public double? Volume { get; set; }
    }

    private readonly FakeAudio _audio = new();
    private readonly FakeMemory _memory = new();
    private DuckOptions _options = new(true, false, ["discord"], ["obs64"], Apps, 0.15, 0);

    private DuckEngine Duck() => new(_audio, _memory, () => _options);

    private static async Task Ticks(DuckEngine engine, int count, bool playing = true)
    {
        for (var i = 0; i < count; i++) await engine.TickAsync(playing);
    }

    [Fact]
    public async Task Ducks_while_a_listed_app_makes_sound_and_restores_after_silence()
    {
        var engine = Duck();
        _audio.Sessions.Add(new AudioSession("Discord", 1, 0.3));

        await Ticks(engine, 2);
        Assert.Equal(("YouTube Music", 0.15), _audio.Changes.Single());

        _audio.Sessions[1] = new AudioSession("Discord", 1, 0);
        await Ticks(engine, 6);
        Assert.Equal(("YouTube Music", 1d), _audio.Changes[^1]);
        Assert.False(engine.IsDucked);
    }

    [Fact]
    public async Task A_silent_open_session_does_not_duck()
    {
        var engine = Duck();
        _audio.Sessions.Add(new AudioSession("Discord", 1, 0));

        await Ticks(engine, 5);

        Assert.Empty(_audio.Changes);
    }

    [Fact]
    public async Task A_short_click_does_not_duck()
    {
        var engine = Duck();
        _audio.Sessions.Add(new AudioSession("Discord", 1, 0.3));
        await engine.TickAsync(true);
        _audio.Sessions[1] = new AudioSession("Discord", 1, 0);
        await Ticks(engine, 3);

        Assert.Empty(_audio.Changes);
    }

    [Fact]
    public async Task Pauses_in_speech_do_not_bounce_the_volume()
    {
        var engine = Duck();
        _audio.Sessions.Add(new AudioSession("Discord", 1, 0.3));
        await Ticks(engine, 2);

        _audio.Sessions[1] = new AudioSession("Discord", 1, 0);
        await Ticks(engine, 3);
        _audio.Sessions[1] = new AudioSession("Discord", 1, 0.3);
        await Ticks(engine, 3);

        Assert.Single(_audio.Changes);
    }

    [Fact]
    public async Task Apps_outside_the_whitelist_do_not_duck()
    {
        var engine = Duck();
        _audio.Sessions.Add(new AudioSession("VRChat", 1, 0.5));

        await Ticks(engine, 5);

        Assert.Empty(_audio.Changes);
    }

    [Fact]
    public async Task The_blacklist_mode_ducks_for_everything_but_the_list()
    {
        _options = _options with { UseBlacklist = true };
        var engine = Duck();
        _audio.Sessions.Add(new AudioSession("obs64", 1, 0.5));
        await Ticks(engine, 5);
        Assert.Empty(_audio.Changes);

        _audio.Sessions.Add(new AudioSession("VRChat", 1, 0.5));
        await Ticks(engine, 2);
        Assert.Single(_audio.Changes);
    }

    [Fact]
    public async Task Music_never_ducks_itself()
    {
        _options = _options with { UseBlacklist = true };
        var engine = Duck();

        await Ticks(engine, 5);

        Assert.Empty(_audio.Changes);
    }

    [Fact]
    public async Task Paused_music_is_left_alone()
    {
        var engine = Duck();
        _audio.Sessions.Add(new AudioSession("Discord", 1, 0.3));

        await Ticks(engine, 5, playing: false);

        Assert.Empty(_audio.Changes);
    }

    [Fact]
    public async Task Keeps_the_users_own_lower_volume()
    {
        _audio.Sessions[0] = new AudioSession("YouTube Music", 0.6, 0.2);
        var engine = Duck();
        _audio.Sessions.Add(new AudioSession("Discord", 1, 0.3));
        await Ticks(engine, 2);

        await engine.ReleaseAsync();

        Assert.Equal(("YouTube Music", 0.6), _audio.Changes[^1]);
    }

    [Fact]
    public async Task Turning_ducking_off_restores_the_music()
    {
        var engine = Duck();
        _audio.Sessions.Add(new AudioSession("Discord", 1, 0.3));
        await Ticks(engine, 2);

        _options = _options with { Enabled = false };
        await engine.TickAsync(true);

        Assert.Equal(1d, _audio.Changes[^1].Volume);
    }

    [Fact]
    public async Task A_crash_while_ducked_is_repaired_on_start()
    {
        _memory.Process = "YouTube Music";
        _memory.Volume = 0.8;

        await Duck().ReleaseAsync();

        Assert.Equal(("YouTube Music", 0.8), _audio.Changes.Single());
        Assert.Null(_memory.Process);
    }
}
