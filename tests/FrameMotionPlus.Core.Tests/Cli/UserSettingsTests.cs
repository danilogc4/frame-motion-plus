using FrameMotionPlus.App.Capture;
using FrameMotionPlus.App.Cli;
using FrameMotionPlus.App.Play;
using FrameMotionPlus.Core.Settings;

namespace FrameMotionPlus.Core.Tests.Cli;

/// <summary>Saved settings: the file, <c>fmp settings show|path|set|unset</c>, and how a file is applied to capture and play.</summary>
public sealed class UserSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fmp-user-settings-" + Guid.NewGuid().ToString("N"));

    public UserSettingsTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string FilePath => Path.Combine(_directory, "sub", "settings.json");

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = CommandDispatcher.Run(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private string Write(string json)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Tests_never_see_the_owners_config_directory()
    {
        var path = UserSettings.DefaultPath();
        Assert.NotNull(path);
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home))
        {
            Assert.False(path.StartsWith(Path.Combine(home, ".config"), StringComparison.Ordinal), path);
        }

        Assert.Contains("fmp-test-config-", path, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("fmp", "settings.json"), path, StringComparison.Ordinal);
    }

    [Fact]
    public void Set_creates_the_file_and_directory_keeps_other_keys_and_show_says_where_values_come_from()
    {
        Assert.Equal(0, Run("settings", "set", "capture.mode", "4K30-HDR", "--settings", FilePath).Exit);
        Assert.Equal(0, Run("settings", "set", "audio.sink", "inzone", "--settings", FilePath).Exit);
        Assert.Equal(0, Run("settings", "set", "hdr.peakNits", "2000", "--settings", FilePath).Exit);
        Assert.Equal(0, Run("settings", "set", "generator.quality", "HIGH", "--settings", FilePath).Exit);
        Assert.Equal(0, Run("settings", "set", "display.fullscreen", "false", "--settings", FilePath).Exit);
        Assert.Equal(0, Run("settings", "set", "audio.offsetMs", "-12.5", "--settings", FilePath).Exit);

        var loaded = UserSettings.Load(FilePath, mustExist: true);
        Assert.Equal("4k30-hdr", loaded.Settings.Capture.Mode);
        Assert.Equal("inzone", loaded.Settings.Audio.Sink);
        Assert.Equal(2000, loaded.Settings.Hdr.PeakNits);
        Assert.Equal(FrameMotionPlus.Core.Abstractions.FrameGenQuality.High, loaded.Settings.Generator.Quality);
        Assert.False(loaded.Settings.Display.Fullscreen);
        Assert.Equal(-12.5, loaded.Settings.Audio.OffsetMs);
        Assert.Equal(FmpSettings.CurrentVersion, loaded.Settings.Version);
        Assert.Empty(loaded.Unknown);

        var (exit, show, _) = Run("settings", "show", "--settings", FilePath);
        Assert.Equal(0, exit);
        Assert.Contains(FilePath, show, StringComparison.Ordinal);
        Assert.Matches(@"capture\.mode\s+4k30-hdr\s+from the file", show);
        Assert.Matches(@"audio\.sink\s+inzone\s+from the file", show);
        Assert.Matches(@"generator\.multiplier\s+\(2\)\s+default", show);

        Assert.Equal(0, Run("settings", "unset", "audio.sink", "--settings", FilePath).Exit);
        loaded = UserSettings.Load(FilePath, mustExist: true);
        Assert.Null(loaded.Settings.Audio.Sink);
        Assert.Equal(-12.5, loaded.Settings.Audio.OffsetMs);
        Assert.Equal("4k30-hdr", loaded.Settings.Capture.Mode);
        Assert.Contains("was not set", Run("settings", "unset", "audio.sink", "--settings", FilePath).Out, StringComparison.Ordinal);
        Assert.Equal(FilePath + Environment.NewLine, Run("settings", "path", "--settings", FilePath).Out);
    }

    [Theory]
    [InlineData("capture.mode", "8k240", "--mode")]
    [InlineData("hdr.peakNits", "50", "--hdr-peak-nits")]
    [InlineData("audio.offsetMs", "900", "--audio-offset-ms")]
    [InlineData("audio.mode", "loud", "--audio")]
    [InlineData("generator.multiplier", "9", "--multiplier")]
    [InlineData("display.clock", "sometimes", "--clock")]
    [InlineData("display.window", "huge", "--window")]
    [InlineData("generator.quality", "ultra", "")]
    [InlineData("display.fullscreen", "maybe", "display.fullscreen")]
    [InlineData("generator.shotChangeDetection", "nonsense", "'--scd' expects on or off")]
    public void Set_checks_a_value_as_the_command_line_does_and_leaves_the_file_alone(string key, string value, string message)
    {
        Assert.Equal(0, Run("settings", "set", "capture.mode", "1440p60-hdr", "--settings", FilePath).Exit);
        var before = File.ReadAllText(FilePath);
        var (exit, _, err) = Run("settings", "set", key, value, "--settings", FilePath);
        Assert.Equal(CommandDispatcher.ExitUsage, exit);
        Assert.Contains(key, err, StringComparison.Ordinal);
        Assert.Contains(message, err, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(FilePath));
    }

    [Fact]
    public void An_unknown_key_cannot_be_set()
    {
        var (exit, _, err) = Run("settings", "set", "audio.sinc", "x", "--settings", FilePath);
        Assert.Equal(CommandDispatcher.ExitUsage, exit);
        Assert.Contains("Unknown setting 'audio.sinc'", err, StringComparison.Ordinal);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void A_malformed_file_is_one_error_naming_the_file_and_position_and_is_never_overwritten()
    {
        var path = Write("{\n  \"capture\": { \"mode\": \"4k30-hdr\" },\n  \"audio\": { \"sink\": \"x\" \n}\n");
        var (exit, _, err) = Run("capture", "--settings", path, "--audio", "off");
        Assert.Equal(CommandDispatcher.ExitUsage, exit);
        Assert.Contains(path, err, StringComparison.Ordinal);
        Assert.Contains("LineNumber: 4", err, StringComparison.Ordinal);

        var before = File.ReadAllText(path);
        Assert.Equal(CommandDispatcher.ExitUsage, Run("settings", "set", "hdr.peakNits", "2000", "--settings", path).Exit);
        Assert.Equal(before, File.ReadAllText(path));

        var wrongType = Write("""{ "hdr": { "peakNits": "bright" } }""");
        Assert.Contains("peakNits", Run("play", "x.mkv", "--settings", wrongType).Err, StringComparison.Ordinal);
        var badValue = Write("""{ "capture": { "mode": "8k240" } }""");
        var bad = Run("capture", "--settings", badValue);
        Assert.Equal(CommandDispatcher.ExitUsage, bad.Exit);
        Assert.Contains("capture.mode: Option '--mode' expects", bad.Err, StringComparison.Ordinal);
        var future = Write("""{ "version": 2 }""");
        Assert.Contains("version must be 1", Run("capture", "--settings", future).Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_keys_are_reported_and_skipped()
    {
        var path = Write("""{ "audio": { "sinc": "x", "sink": "inzone" }, "colour": 1 }""");
        var loaded = UserSettings.Load(path, mustExist: true);
        Assert.Equal(["audio.sinc", "colour"], loaded.Unknown);
        Assert.Equal("inzone", loaded.Values["audio.sink"]);
        var applied = UserSettings.Apply(SettingsTarget.Capture, [], loaded);
        Assert.Contains("unknown keys skipped: audio.sinc, colour", UserSettings.Describe(loaded, applied), StringComparison.Ordinal);
        Assert.Contains("audio.sinc", Run("settings", "show", "--settings", path).Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_saved_capture_rate_does_not_override_a_mode_typed_on_the_command_line()
    {
        // Saved 30 (with or without a saved mode): --mode 1440p60-hdr keeps its own 59.94, the saved rate is reported unused.
        var path = Write("""{ "capture": { "mode": "4k30-hdr", "fps": "30" } }""");
        var loaded = UserSettings.Load(path, mustExist: true);
        var applied = UserSettings.Apply(SettingsTarget.Capture, ["--mode", "1440p60-hdr"], loaded);
        Assert.Equal(new FrameMotionPlus.Core.Time.FrameRate(60000, 1001), CaptureOptions.Parse(applied.Args).CaptureRate);
        Assert.Contains(applied.Overridden, o => o.StartsWith("capture.fps (--mode given", StringComparison.Ordinal));

        // Without --mode the saved rate applies to the saved mode, and --capture-fps still beats it.
        Assert.Equal(FrameMotionPlus.Core.Time.FrameRate.FromHz(30), CaptureOptions.Parse(UserSettings.Apply(SettingsTarget.Capture, [], loaded).Args).CaptureRate);
        Assert.Equal(new FrameMotionPlus.Core.Time.FrameRate(30000, 1001),
            CaptureOptions.Parse(UserSettings.Apply(SettingsTarget.Capture, ["--capture-fps", "29.97"], loaded).Args).CaptureRate);
    }

    [Fact]
    public void A_missing_default_file_is_normal_and_changes_nothing()
    {
        var loaded = UserSettings.Load(Path.Combine(_directory, "none", "settings.json"), mustExist: false);
        Assert.False(loaded.Exists);
        string[] args = ["--mode", "4k30-hdr", "--audio", "off", "--windowed"];
        var applied = UserSettings.Apply(SettingsTarget.Capture, args, loaded);
        Assert.Equal(args, applied.Args);
        Assert.Contains("settings    none (no file at", UserSettings.Describe(loaded, applied), StringComparison.Ordinal);
        Assert.Equal(CaptureOptions.Parse(args), CaptureOptions.Parse(applied.Args));
    }

    [Fact]
    public void Bare_capture_takes_the_saved_device_mode_sound_and_hdr()
    {
        var path = Write("""
            { "capture": { "device": "/dev/video7", "mode": "4k30-hdr" }, "audio": { "sink": "inzone", "offsetMs": 20 },
              "hdr": { "peakNits": 2000, "maxFall": 400 }, "generator": { "quality": "high", "multiplier": 3 }, "display": { "clock": "fixed" } }
            """);
        var loaded = UserSettings.Load(path, mustExist: true);
        var applied = UserSettings.Apply(SettingsTarget.Capture, [], loaded);
        var o = CaptureOptions.Parse(applied.Args);
        Assert.Equal("/dev/video7", o.Device);
        Assert.Equal("4k30-hdr", o.Preset?.Name);
        Assert.Equal("inzone", o.AudioSink);
        Assert.Equal(AudioChoice.On, o.Audio);
        Assert.Equal(20, o.AudioOffsetMs);
        Assert.Equal(2000, o.Color.PeakNits);
        Assert.Equal(400, o.Color.MaxFall);
        Assert.Equal(FrameMotionPlus.Core.Abstractions.FrameGenQuality.High, o.Quality);
        Assert.Equal(3, o.Multiplier);
        Assert.Equal(FrameMotionPlus.Pipeline.RefreshMode.Fixed, o.Refresh);
        Assert.True(o.Fullscreen);
        var line = UserSettings.Describe(loaded, applied);
        Assert.Contains(path, line, StringComparison.Ordinal);
        Assert.Contains("capture.mode=4k30-hdr", line, StringComparison.Ordinal);
        Assert.Contains("audio.sink=inzone", line, StringComparison.Ordinal);
        Assert.Contains("hdr.peakNits=2000", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Quality_switching_is_an_option_of_both_commands_and_a_saved_setting()
    {
        // The default, the two options and their contradiction.
        Assert.Equal(PlayOptions.QualitySwitchDefault, CaptureOptions.Parse([]).QualitySwitch);
        Assert.Equal(PlayOptions.QualitySwitchDefault, PlayOptions.Parse(["clip.mkv"]).QualitySwitch);
        Assert.False(CaptureOptions.Parse(["--no-quality-switch"]).QualitySwitch);
        Assert.True(CaptureOptions.Parse(["--quality-switch"]).QualitySwitch);
        Assert.False(PlayOptions.Parse(["clip.mkv", "--no-quality-switch"]).QualitySwitch);
        Assert.True(PlayOptions.Parse(["clip.mkv", "--quality-switch", "--quality", "high"]).QualitySwitch);
        Assert.Throws<UsageException>(() => CaptureOptions.Parse(["--quality-switch", "--no-quality-switch"]));
        Assert.Throws<UsageException>(() => PlayOptions.Parse(["clip.mkv", "--quality-switch", "--no-quality-switch"]));

        // Saved: generator.qualitySwitch, checked as a boolean, applied as the option unless one is typed.
        Assert.Equal(0, Run("settings", "set", "generator.qualitySwitch", "OFF", "--settings", FilePath).Exit);
        var loaded = UserSettings.Load(FilePath, mustExist: true);
        Assert.False(loaded.Settings.Generator.QualitySwitch);
        Assert.Equal("false", loaded.Values["generator.qualitySwitch"]);
        var applied = UserSettings.Apply(SettingsTarget.Capture, [], loaded);
        Assert.False(CaptureOptions.Parse(applied.Args).QualitySwitch);
        Assert.Contains("generator.qualitySwitch=false", applied.Applied);
        Assert.False(PlayOptions.Parse(UserSettings.Apply(SettingsTarget.Play, ["clip.mkv"], loaded).Args).QualitySwitch);
        applied = UserSettings.Apply(SettingsTarget.Capture, ["--quality-switch"], loaded);
        Assert.True(CaptureOptions.Parse(applied.Args).QualitySwitch);
        Assert.Contains(applied.Overridden, o => o.StartsWith("generator.qualitySwitch", StringComparison.Ordinal));
        Assert.Equal(2, Run("settings", "set", "generator.qualitySwitch", "maybe", "--settings", FilePath).Exit);
        Assert.Contains("\"qualitySwitch\": false", File.ReadAllText(FilePath), StringComparison.Ordinal);
        Assert.Equal(0, Run("settings", "set", "generator.qualitySwitch", "true", "--settings", FilePath).Exit);
        Assert.True(UserSettings.Load(FilePath, mustExist: true).Settings.Generator.QualitySwitch);
        var show = Run("settings", "show", "--settings", FilePath);
        Assert.Contains("generator.qualitySwitch", show.Out, StringComparison.Ordinal);

        // The help names the key, the options and the setting.
        var help = Run("--help").Out;
        Assert.Contains("--no-quality-switch", help, StringComparison.Ordinal);
        Assert.Contains("generator.qualitySwitch", help, StringComparison.Ordinal);
        Assert.Contains(" Q ", help, StringComparison.Ordinal);
    }

    /// <summary><c>--ceiling-pacing on|off</c> on both commands (on by default), and in the help.</summary>
    [Fact]
    public void Ceiling_pacing_is_an_option_of_both_commands_on_by_default()
    {
        Assert.True(CaptureOptions.Parse([]).CeilingPacing);
        Assert.True(PlayOptions.Parse(["clip.mkv"]).CeilingPacing);
        Assert.False(CaptureOptions.Parse(["--ceiling-pacing", "off"]).CeilingPacing);
        Assert.True(CaptureOptions.Parse(["--ceiling-pacing", "on"]).CeilingPacing);
        Assert.False(PlayOptions.Parse(["clip.mkv", "--ceiling-pacing=off"]).CeilingPacing);
        Assert.Contains("'--ceiling-pacing' expects on or off, got 'auto'", Assert.Throws<UsageException>(() => CaptureOptions.Parse(["--ceiling-pacing", "auto"])).Message, StringComparison.Ordinal);
        Assert.Throws<UsageException>(() => PlayOptions.Parse(["clip.mkv", "--ceiling-pacing"]));
        var help = Run("--help").Out;
        Assert.Contains("--ceiling-pacing <on|off> Variable refresh with the slots at the output's ceiling", help, StringComparison.Ordinal);
        Assert.Contains("--late-generated-ms, --ceiling-pacing, --cuda-sync", help, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--scd on|off</c> (VFG's automatic shot-change detection) on both commands, off by default for a capture (a game's
    /// fast motion is what it takes for a cut) and on for a file; <c>generator.shotChangeDetection</c> saves it.
    /// </summary>
    [Fact]
    public void Shot_change_detection_is_an_option_of_both_commands_and_a_saved_setting()
    {
        // The two defaults, both values, the option's other spellings and a bad value.
        Assert.False(CaptureOptions.ShotChangeDetectionDefault);
        Assert.True(PlayOptions.ShotChangeDetectionDefault);
        Assert.False(CaptureOptions.Parse([]).ShotChangeDetection);
        Assert.True(PlayOptions.Parse(["clip.mkv"]).ShotChangeDetection);
        Assert.True(CaptureOptions.Parse(["--scd", "on"]).ShotChangeDetection);
        Assert.False(CaptureOptions.Parse(["--scd", "off"]).ShotChangeDetection);
        Assert.True(CaptureOptions.Parse(["--scd=ON"]).ShotChangeDetection);
        Assert.False(PlayOptions.Parse(["clip.mkv", "--scd", "off"]).ShotChangeDetection);
        Assert.True(PlayOptions.Parse(["clip.mkv", "--scd", "on"]).ShotChangeDetection);
        Assert.False(PlayOptions.Parse(["clip.mkv", "--scd", "false"]).ShotChangeDetection);
        Assert.Contains("'--scd' expects on or off, got 'sometimes'", Assert.Throws<UsageException>(() => CaptureOptions.Parse(["--scd", "sometimes"])).Message, StringComparison.Ordinal);
        Assert.Contains("'--scd' expects on or off", Assert.Throws<UsageException>(() => PlayOptions.Parse(["clip.mkv", "--scd", "maybe"])).Message, StringComparison.Ordinal);
        Assert.Throws<UsageException>(() => CaptureOptions.Parse(["--scd"]));

        // The gate is the milestone run as it was measured (detection on): --scd is refused with it, like --quality.
        Assert.True(PlayOptions.Parse(["clip.mkv", "--gate"]).ShotChangeDetection);
        Assert.Contains("--scd", Assert.Throws<UsageException>(() => PlayOptions.Parse(["clip.mkv", "--gate", "--scd", "off"])).Message, StringComparison.Ordinal);

        // Saved: checked as --scd is, kept in the option's words, applied to both commands unless --scd is typed.
        Assert.Equal(0, Run("settings", "set", "generator.shotChangeDetection", "TRUE", "--settings", FilePath).Exit);
        Assert.Contains("\"shotChangeDetection\": \"on\"", File.ReadAllText(FilePath), StringComparison.Ordinal);
        var loaded = UserSettings.Load(FilePath, mustExist: true);
        Assert.Equal("on", loaded.Settings.Generator.ShotChangeDetection);
        var applied = UserSettings.Apply(SettingsTarget.Capture, [], loaded);
        Assert.True(CaptureOptions.Parse(applied.Args).ShotChangeDetection);
        Assert.Contains("generator.shotChangeDetection=on", applied.Applied);
        Assert.Contains("generator.shotChangeDetection=on", UserSettings.Describe(loaded, applied), StringComparison.Ordinal);
        applied = UserSettings.Apply(SettingsTarget.Capture, ["--scd", "off"], loaded);
        Assert.False(CaptureOptions.Parse(applied.Args).ShotChangeDetection);
        Assert.Contains(applied.Overridden, o => o.StartsWith("generator.shotChangeDetection (--scd given", StringComparison.Ordinal));

        Assert.Equal(0, Run("settings", "set", "generator.shotChangeDetection", "off", "--settings", FilePath).Exit);
        loaded = UserSettings.Load(FilePath, mustExist: true);
        Assert.Equal("off", loaded.Values["generator.shotChangeDetection"]);
        Assert.False(PlayOptions.Parse(UserSettings.Apply(SettingsTarget.Play, ["clip.mkv"], loaded).Args).ShotChangeDetection);
        Assert.True(PlayOptions.Parse(UserSettings.Apply(SettingsTarget.Play, ["clip.mkv", "--scd=on"], loaded).Args).ShotChangeDetection);
        Assert.False(CaptureOptions.Parse(UserSettings.Apply(SettingsTarget.Capture, [], loaded).Args).ShotChangeDetection);

        // --gate keeps its own (VFG Medium 2x as measured): the saved value is reported unused.
        var gate = UserSettings.Apply(SettingsTarget.Play, ["clip.mkv", "--gate"], loaded);
        Assert.True(PlayOptions.Parse(gate.Args).ShotChangeDetection);
        Assert.Contains(gate.Overridden, o => o.StartsWith("generator.shotChangeDetection (--gate", StringComparison.Ordinal));

        // A bad value is refused and the file left alone; a hand-edited one is checked at load.
        Assert.Equal(CommandDispatcher.ExitUsage, Run("settings", "set", "generator.shotChangeDetection", "nonsense", "--settings", FilePath).Exit);
        Assert.Equal("off", UserSettings.Load(FilePath, mustExist: true).Settings.Generator.ShotChangeDetection);
        var bad = Run("capture", "--settings", Write("""{ "generator": { "shotChangeDetection": "sometimes" } }"""));
        Assert.Equal(CommandDispatcher.ExitUsage, bad.Exit);
        Assert.Contains("generator.shotChangeDetection: Option '--scd' expects on or off", bad.Err, StringComparison.Ordinal);
        Assert.Matches(@"generator\.shotChangeDetection\s+off\s+from the file", Run("settings", "show", "--settings", FilePath).Out);
        Assert.Matches(@"generator\.shotChangeDetection\s+\(capture: off, play: on\)\s+default", Run("settings", "show", "--settings", Write("{}")).Out);

        // The help names the option for both commands, the defaults and the key.
        var help = Run("--help").Out;
        Assert.Contains("--scd <on|off>            VFG's automatic shot-change detection. On,", help, StringComparison.Ordinal);
        Assert.Contains("(play: on; capture: off)", help, StringComparison.Ordinal);
        Assert.Contains("--latch-ms, --scd, --late-generated-ms", help, StringComparison.Ordinal);
        Assert.Contains("generator.shotChangeDetection (--scd: on or off)", help, StringComparison.Ordinal);
    }

    /// <summary>The start-up line says whether VFG's shot-change detection is on, and names each command's default.</summary>
    [Fact]
    public void The_start_up_line_says_how_shot_change_detection_is_set()
    {
        var captureOff = PlayPrinter.DescribeShotChangeDetection(on: false, capture: true);
        var captureOn = PlayPrinter.DescribeShotChangeDetection(on: true, capture: true);
        var playOn = PlayPrinter.DescribeShotChangeDetection(on: true, capture: false);
        var playOff = PlayPrinter.DescribeShotChangeDetection(on: false, capture: false);
        Assert.StartsWith("automatic shot-change detection off (--scd off, a capture's default:", captureOff, StringComparison.Ordinal);
        Assert.Contains("a real cut shows one morphed frame", captureOff, StringComparison.Ordinal);
        Assert.StartsWith("automatic shot-change detection on (--scd on:", captureOn, StringComparison.Ordinal);
        Assert.Contains("--scd off keeps generating", captureOn, StringComparison.Ordinal);
        Assert.StartsWith("automatic shot-change detection on (--scd on, play's default:", playOn, StringComparison.Ordinal);
        Assert.StartsWith("automatic shot-change detection off (--scd off:", playOff, StringComparison.Ordinal);
        Assert.DoesNotContain("default", playOff, StringComparison.Ordinal);
        Assert.DoesNotContain("default", captureOn, StringComparison.Ordinal);
    }

    [Fact]
    public void The_command_line_wins_over_the_file()
    {
        var path = Write("""
            { "capture": { "device": "/dev/video7", "mode": "4k30-hdr" }, "audio": { "mode": "off", "sink": "inzone" },
              "hdr": { "peakNits": 2000 }, "generator": { "multiplier": 3 }, "display": { "fullscreen": false, "window": "960x540" } }
            """);
        var loaded = UserSettings.Load(path, mustExist: true);

        var o = CaptureOptions.Parse(UserSettings.Apply(SettingsTarget.Capture, ["/dev/video2", "--size", "1920x1080", "--hdr-peak-nits", "1000", "--output-hz", "120", "--fullscreen"], loaded).Args);
        Assert.Equal("/dev/video2", o.Device);
        Assert.Null(o.Preset);
        Assert.Equal(1920, o.Request.Width);
        Assert.Equal(1000, o.Color.PeakNits);
        Assert.Equal(120, o.OutputHz);
        Assert.True(o.Fullscreen);
        Assert.Equal(1280, o.WindowWidth);
        Assert.Equal(AudioChoice.Off, o.Audio);
        Assert.Null(o.AudioSink);

        // Saved off, but a sink on the command line turns the sound on (as it does there).
        o = CaptureOptions.Parse(UserSettings.Apply(SettingsTarget.Capture, ["--audio-sink", "hdmi"], loaded).Args);
        Assert.Equal(AudioChoice.On, o.Audio);
        Assert.Equal("hdmi", o.AudioSink);

        // --audio on brings the saved sink back; a saved window applies to a saved windowed choice.
        var applied = UserSettings.Apply(SettingsTarget.Capture, ["--audio", "on"], loaded);
        o = CaptureOptions.Parse(applied.Args);
        Assert.Equal("inzone", o.AudioSink);
        Assert.False(o.Fullscreen);
        Assert.Equal(960, o.WindowWidth);
        Assert.Contains(applied.Overridden, s => s.StartsWith("audio.mode", StringComparison.Ordinal));

        // Offscreen: no window settings.
        o = CaptureOptions.Parse(UserSettings.Apply(SettingsTarget.Capture, ["--offscreen"], loaded).Args);
        Assert.True(o.Offscreen);
        Assert.False(o.Fullscreen);
    }

    [Fact]
    public void A_saved_window_size_alone_does_not_make_capture_windowed()
    {
        var loaded = UserSettings.Load(Write("""{ "display": { "window": "960x540" } }"""), mustExist: true);
        var o = CaptureOptions.Parse(UserSettings.Apply(SettingsTarget.Capture, [], loaded).Args);
        Assert.True(o.Fullscreen);
        o = CaptureOptions.Parse(UserSettings.Apply(SettingsTarget.Capture, ["--windowed"], loaded).Args);
        Assert.False(o.Fullscreen);
        Assert.Equal(960, o.WindowWidth);
    }

    [Fact]
    public void Play_takes_the_generator_and_display_keys_only_and_the_gate_keeps_its_own()
    {
        var loaded = UserSettings.Load(Write("""
            { "capture": { "mode": "4k30-hdr" }, "audio": { "sink": "inzone" }, "hdr": { "peakNits": 2000 },
              "generator": { "quality": "high", "multiplier": 4 }, "display": { "window": "960x540", "clock": "fixed" } }
            """), mustExist: true);
        var o = PlayOptions.Parse(UserSettings.Apply(SettingsTarget.Play, ["clip.mkv"], loaded).Args);
        Assert.Equal(FrameMotionPlus.Core.Abstractions.FrameGenQuality.High, o.Quality);
        Assert.Equal(4, o.Multiplier);
        Assert.Equal(960, o.WindowWidth);
        Assert.Equal(FrameMotionPlus.Pipeline.RefreshMode.Fixed, o.Refresh);

        var gate = PlayOptions.Parse(UserSettings.Apply(SettingsTarget.Play, ["clip.mkv", "--gate"], loaded).Args);
        Assert.Equal(FrameMotionPlus.Core.Abstractions.FrameGenQuality.Medium, gate.Quality);
        Assert.Equal(2, gate.Multiplier);
    }

    [Fact]
    public void No_settings_ignores_the_file_and_cannot_be_combined_with_settings()
    {
        var loaded = UserSettings.LoadFor(["--mode", "4k30-hdr", "--no-settings"], out var rest);
        Assert.True(loaded.Ignored);
        Assert.Equal(["--mode", "4k30-hdr"], rest);
        Assert.Contains("--no-settings", UserSettings.Describe(loaded, UserSettings.Apply(SettingsTarget.Capture, rest, loaded)), StringComparison.Ordinal);
        Assert.Throws<UsageException>(() => UserSettings.LoadFor(["--settings", "x.json", "--no-settings"], out _));
    }

    /// <summary>
    /// What <c>fmp settings</c> prints, saved and used as a settings file, runs exactly as no file: the generator's backend
    /// and quality (which the file model always holds) at the command line's defaults are not counted as saved.
    /// </summary>
    [Fact]
    public void The_printed_defaults_used_as_a_settings_file_run_as_no_file()
    {
        var (exit, printed, _) = Run("settings");
        Assert.Equal(0, exit);
        Assert.Contains("\"backend\": \"auto\"", printed, StringComparison.Ordinal);
        var path = Write(printed);
        var loaded = UserSettings.Load(path, mustExist: true);
        Assert.Empty(loaded.Values);
        var capture = UserSettings.Apply(SettingsTarget.Capture, [], loaded);
        Assert.Empty(capture.Args);
        Assert.Empty(capture.Applied);
        Assert.Equal(CaptureOptions.Parse([]), CaptureOptions.Parse(capture.Args));
        Assert.Contains("no value used from it", UserSettings.Describe(loaded, capture), StringComparison.Ordinal);
        var play = UserSettings.Apply(SettingsTarget.Play, ["clip.mkv"], loaded);
        Assert.Equal(["clip.mkv"], play.Args);
        Assert.Null(UserSettings.Blame(loaded, capture));

        // A backend or quality other than the default is still a saved value.
        var vfg = UserSettings.Load(Write("""{ "generator": { "backend": "vfg", "quality": "High" } }"""), mustExist: true);
        Assert.Equal("vfg", vfg.Values["generator.backend"]);
        Assert.Equal("high", vfg.Values["generator.quality"]);
    }

    /// <summary>
    /// A saved value that fails only once the command runs (a device that is not there) is reported with the key and the
    /// file it came from; without a file nothing extra is said.
    /// </summary>
    [Fact]
    public void A_failure_from_a_saved_value_names_the_key_and_the_file()
    {
        var path = Write("""{ "capture": { "device": "/dev/video99" }, "audio": { "mode": "off" } }""");
        var loaded = UserSettings.Load(path, mustExist: true);
        var blame = UserSettings.Blame(loaded, UserSettings.Apply(SettingsTarget.Capture, [], loaded));
        Assert.NotNull(blame);
        Assert.Contains("capture.device=/dev/video99", blame, StringComparison.Ordinal);
        Assert.Contains(path, blame, StringComparison.Ordinal);

        // The command itself (it stops at the device check, before anything is opened; skipped where capture or CUDA is absent).
        var (exit, _, err) = Run("capture", "--settings", path, "--windowed");
        if (!err.Contains("no such capture device", StringComparison.Ordinal))
        {
            Assert.Skip($"capture stopped earlier here (exit {exit}): {err.Trim()}");
        }

        Assert.Contains("/dev/video99", err, StringComparison.Ordinal);
        Assert.Contains($"saved settings used from {path}: capture.device=/dev/video99", err, StringComparison.Ordinal);
        var (_, _, plain) = Run("capture", "/dev/video99", "--no-settings", "--audio", "off", "--windowed");
        Assert.Contains("no such capture device /dev/video99", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("saved settings", plain, StringComparison.Ordinal);
    }

    /// <summary><c>set</c> and <c>unset</c> on a hand-edited file with comments say the comments were not kept.</summary>
    [Fact]
    public void Set_and_unset_say_when_they_drop_a_files_comments()
    {
        var path = Write("""
            // my settings
            { "audio": { "sink": "inzone" /* the headset */ }, "hdr": { "peakNits": 2000 } }
            """);
        var (exit, output, _) = Run("settings", "set", "capture.mode", "4k30-hdr", "--settings", path);
        Assert.Equal(0, exit);
        Assert.Contains(SettingsCommand.CommentsDropped, output, StringComparison.Ordinal);
        Assert.DoesNotContain("//", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal("inzone", UserSettings.Load(path, mustExist: true).Values["audio.sink"]);

        // Now without comments: nothing said.
        Assert.DoesNotContain("comments", Run("settings", "set", "hdr.peakNits", "1000", "--settings", path).Out, StringComparison.Ordinal);
        var commented = Write("""{ "hdr": { "peakNits": 2000 } } // end""");
        var unset = Run("settings", "unset", "hdr.peakNits", "--settings", commented);
        Assert.Contains("removed", unset.Out, StringComparison.Ordinal);
        Assert.Contains(SettingsCommand.CommentsDropped, unset.Out, StringComparison.Ordinal);

        // A "//" inside a string is not a comment.
        Assert.False(UserSettings.HasComments("""{ "audio": { "sink": "a//b /* c */" } }"""));
        Assert.True(UserSettings.HasComments("""{ "a": 1 /* c */ }"""));
    }

    [Fact]
    public void Every_key_is_known_to_the_settings_file()
    {
        var known = FmpSettingsSerializer.KnownKeys();
        foreach (var key in UserSettings.Keys)
        {
            Assert.Contains(key.Name, known);
        }

        Assert.Contains("fmp settings set", Run("--help").Out, StringComparison.Ordinal);
    }
}
