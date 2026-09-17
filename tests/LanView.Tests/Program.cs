using LanView.Windows;
using System.Drawing;

var failures = 0;
var passed = 0;
var privateProfile = new Profile("192.168.50.100", "test_user", "");

Run("Application icon is embedded as a complete multi-size ICO", () =>
{
    var assembly = typeof(AppIcon).Assembly;
    Require(assembly.GetManifestResourceNames().Contains(AppIcon.ResourceName),
        "The application icon is not embedded with its stable resource name.");
    using var stream = assembly.GetManifestResourceStream(AppIcon.ResourceName)!;
    using var reader = new BinaryReader(stream);
    Require(reader.ReadUInt16() == 0 && reader.ReadUInt16() == 1, "Invalid ICO header.");
    var count = reader.ReadUInt16();
    Require(count >= 4 && 6L + count * 16 <= stream.Length, "The ICO image directory is incomplete.");
    var sizes = new HashSet<int>();
    var directoryEnd = 6L + count * 16;
    for (var index = 0; index < count; index++)
    {
        var widthByte = reader.ReadByte();
        var heightByte = reader.ReadByte();
        var width = widthByte == 0 ? 256 : widthByte;
        var height = heightByte == 0 ? 256 : heightByte;
        reader.ReadBytes(6); // Color count, reserved byte, planes and bit depth.
        var bytes = reader.ReadUInt32();
        var offset = reader.ReadUInt32();
        Require(width == height, "Application icon images must be square.");
        Require(bytes >= 8 && offset >= directoryEnd && (long)offset + bytes <= stream.Length,
            "An ICO image points outside the embedded resource.");
        sizes.Add(width);
        var nextEntry = stream.Position;
        stream.Position = offset;
        var header = reader.ReadBytes(8);
        var isPng = header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var isDib = BitConverter.ToUInt32(header) is 40 or 108 or 124;
        Require(isPng || isDib, "An ICO image is neither PNG nor a DIB bitmap.");
        if (isPng)
        {
            // Decode each frame, including 256px entries encoded as zero in ICO.
            stream.Position = offset;
            using var pngStream = new MemoryStream(reader.ReadBytes(checked((int)bytes)));
            using var image = Image.FromStream(pngStream);
            Require(image.Width == width && image.Height == height,
                "A PNG frame does not match its ICO directory dimensions.");
        }
        stream.Position = nextEntry;
    }
    foreach (var size in new[] { 16, 32, 48, 256 })
        Require(sizes.Contains(size), $"The application icon is missing its {size}px image.");
});

Run("Application icons survive stream closure and have independent lifetimes", () =>
{
    using var first = AppIcon.Load();
    using var second = AppIcon.Load();
    Require(!ReferenceEquals(first, second) && first.Handle != second.Handle,
        "Icon loads must return separately owned native resources.");
    first.Dispose();
    foreach (var size in new[] { 16, 32, 48, 128 })
    {
        using var resized = new Icon(second, size, size);
        using var bitmap = resized.ToBitmap();
        Require(bitmap.Width == size && bitmap.Height == size,
            $"The {size}px icon decoded as {bitmap.Width}x{bitmap.Height} after the resource stream closes.");
    }
});

Run("Private IPv4 addresses", () =>
{
    foreach (var host in new[] { "10.1.2.3", "172.16.1.2", "172.31.254.1", "192.168.50.100" })
    {
        Require((privateProfile with { Host = host }).Validate(false) is null,
            $"Rejected private IPv4 address: {host}");
    }
});

Run("Reject public, loopback and ambiguous addresses", () =>
{
    foreach (var host in new[]
    {
        "8.8.8.8", "172.15.1.1", "172.32.1.1", "127.0.0.1", "0.0.0.0",
        "169.254.1.1", "::1", "fe80::1", "localhost", "10.1", "192.168.050.100",
        "0x0a000001", "10.1.1.1;echo test", "10.1.1.1\n", ""
    })
    {
        Require((privateProfile with { Host = host }).Validate(false) is not null,
            $"Accepted invalid LAN address: {host}");
    }
});

Run("SSH user validation", () =>
{
    foreach (var user in new[] { "test_user", "user-name", "User1", "_service" })
    {
        Require((privateProfile with { User = user }).Validate(false) is null,
            $"Rejected valid username: {user}");
    }
    foreach (var user in new[]
    {
        "", "-oProxyCommand=echo", "user;echo", "test user", "user@host",
        "user\n", "user`id`", "$(id)", "user/other", new string('a', 80)
    })
    {
        Require((privateProfile with { User = user }).Validate(false) is not null,
            "Accepted a malformed SSH username.");
    }
});

Run("Profile normalization", () =>
{
    var normalized = new Profile(" 10.1.2.3 ", " test_user ", " C:\\Apps\\Moonlight.exe ").Normalize();
    Require(normalized.Host == "10.1.2.3" && normalized.User == "test_user"
        && normalized.MoonlightPath == "C:\\Apps\\Moonlight.exe", "Whitespace was not normalized.");
    Require(new Profile(null!, null!, null!).Normalize() == Profile.Empty,
        "Missing serialized profile values were not normalized.");
    Require(ProfileStore.FilePath == Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "LanView", "profile.json"),
        "The profile must use the same user directory when launched from different applications.");
});

Run("Moonlight path validation does not execute files", () =>
{
    var testDirectory = Path.Combine(Path.GetTempPath(), $"LanView-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(testDirectory);
    var executable = Path.Combine(testDirectory, "Moonlight test.exe");
    try
    {
        Require(privateProfile.Validate(false) is null, "Inspection unexpectedly needs Moonlight.");
        Require(privateProfile.Validate() is not null, "Streaming accepted a missing executable.");
        Require((privateProfile with { MoonlightPath = "Moonlight.exe" }).Validate() is not null,
            "Streaming accepted a relative executable path.");
        Require((privateProfile with { MoonlightPath = executable }).Validate() is not null,
            "Streaming accepted a nonexistent executable.");
        File.WriteAllBytes(executable, []);
        Require((privateProfile with { MoonlightPath = executable }).Validate() is null,
            "Rejected an existing absolute .exe path containing spaces.");
        Require((privateProfile with { MoonlightPath = executable + ".cmd" }).Validate() is not null,
            "Accepted a non-executable extension.");
    }
    finally
    {
        if (File.Exists(executable)) File.Delete(executable);
        Directory.Delete(testDirectory);
    }
});

Run("Bundled viewer overrides an old saved installation path", () =>
{
    var root = Path.Combine(Path.GetTempPath(), $"LanView-bundle-test-{Guid.NewGuid():N}");
    var viewerDirectory = Path.Combine(root, "tools", "Moonlight");
    var viewer = Path.Combine(viewerDirectory, "Moonlight.exe");
    const string oldPath = @"C:\Old application\Moonlight.exe";
    Directory.CreateDirectory(viewerDirectory);
    try
    {
        Require(ProfileStore.ResolveMoonlightPath(oldPath, root) == oldPath,
            "Source builds lost the saved development viewer path.");
        File.WriteAllBytes(viewer, []);
        Require(ProfileStore.ResolveMoonlightPath(oldPath, root) == viewer,
            "The installed bundle did not replace the old viewer path.");
        Require(ProfileStore.ResolveMoonlightPath("", root) == viewer,
            "A fresh install required a separately selected viewer.");
    }
    finally
    {
        if (File.Exists(viewer)) File.Delete(viewer);
        Directory.Delete(viewerDirectory);
        Directory.Delete(Path.Combine(root, "tools"));
        Directory.Delete(root);
    }
});

Run("Reject malformed host status without throwing", () =>
{
    foreach (var json in new[]
    {
        "", "{", "null", "true", "42", "\"text\"", "[]", "{}",
        "{\"running\":true}",
        "{\"running\":\"true\",\"gpuRuntimeStatus\":\"active\"}",
        "{\"running\":true,\"gpuRuntimeStatus\":null}",
        "{\"running\":true,\"gpuRuntimeStatus\":42}"
    })
    {
        Require(!SessionController.TryParseStatus(json, out var status) && status is null,
            "Accepted malformed status or returned a partial result.");
    }
});

Run("Host status and GPU power states", () =>
{
    foreach (var (raw, expected) in new[]
    {
        ("active", "ativa"), ("suspended", "suspensa"), ("suspending", "suspendendo"),
        ("resuming", "ativando"), ("unknown", "desconhecida")
    })
    {
        var json = $$"""{"running":false,"gpuRuntimeStatus":"{{raw}}"}""";
        Require(SessionController.TryParseStatus(json, out var status), "Rejected a valid host status.");
        Require(status!.State == "Parado" && status.GpuState == expected,
            "Incorrect host or GPU state.");
    }
    Require(SessionController.TryParseStatus("{\"running\":true,\"gpuRuntimeStatus\":\"active\"}", out var active)
        && active!.State == "Host ativo", "A running host was not identified.");
    Require(SessionController.TryParseStatus("{\"running\":true,\"gpuRuntimeStatus\":\"suspended\"}", out var suspended)
        && suspended!.State == "Host ativo" && suspended.GpuState == "suspensa",
        "Starting the host must not imply that the GPU is active.");
});

Run("Local disconnect does not claim a remote GPU state", () =>
{
    using var controller = new SessionController();
    SessionStatus? observed = null;
    controller.StatusChanged += status => observed = status;
    controller.DisconnectAsync().GetAwaiter().GetResult();
    Require(observed is not null && observed.GpuState == "não consultada",
        "Disconnect reported an unobserved remote GPU state.");
});

Run("Disposed controller rejects inspection before validation", () =>
{
    using var controller = new SessionController();
    controller.Dispose();
    try
    {
        // An empty profile also prevents any network access if this guard regresses.
        controller.InspectAsync(Profile.Empty).GetAwaiter().GetResult();
        throw new InvalidOperationException("Inspection succeeded after disposal.");
    }
    catch (ObjectDisposedException) { }
});

Run("Pairing uses the upstream client", () =>
{
    var args = SessionController.ViewerArguments(privateProfile, pairing: true);
    Require(args.SequenceEqual(new[] { "pair", privateProfile.Host }),
        "Pairing must invoke Moonlight pair with exactly one target host.");
});

Run("Desktop stream quality and input settings", () =>
{
    var args = SessionController.ViewerArguments(privateProfile, pairing: false);
    Require(args.Take(3).SequenceEqual(new[] { "stream", privateProfile.Host, "Desktop" }),
        "Stream target must be the selected host's Desktop app.");
    Require(args.Contains("--1080") && ValueAfter(args, "--fps") == "60", "Unexpected resolution or frame rate.");
    Require(ValueAfter(args, "--video-codec") == "HEVC" && args.Contains("--yuv444"),
        "The desktop stream lost its HEVC 4:4:4 request.");
    Require(ValueAfter(args, "--video-decoder") == "hardware", "Hardware decoding was not requested.");
    Require(ValueAfter(args, "--display-mode") == "windowed" && args.Contains("--absolute-mouse"),
        "The stream must open in a window with absolute mouse input.");
    Require(ValueAfter(args, "--capture-system-keys") == "always", "Focused remote window must receive system shortcuts.");
    Require(args.Contains("--no-frame-pacing"), "The low-latency pacing preference was lost.");
});

Run("Fullscreen Alt+Tab uses the viewer's native local escape", () =>
{
    var info = SessionController.ViewerStartInfo(privateProfile, pairing: false, Path.GetTempPath());
    Require(info.Environment["SDL_ALLOW_ALT_TAB_WHILE_GRABBED"] == "1",
        "The fullscreen Alt+Tab escape must be enabled for the video process.");
    Require(info.ArgumentList.SequenceEqual(SessionController.ViewerArguments(privateProfile, pairing: false)),
        "The launch configuration changed the stream arguments.");
    Require(ValueAfter(info.ArgumentList.ToArray(), "--capture-system-keys") == "always",
        "Other system shortcuts must still reach the remote desktop.");
    Require(!info.UseShellExecute && info.CreateNoWindow && info.WorkingDirectory == Path.GetTempPath(),
        "Viewer startup must keep its explicit working directory and avoid a console or shell.");
});

Run("Alt+Tab override is scoped to streaming, not the launcher or pairing", () =>
{
    const string hint = "SDL_ALLOW_ALT_TAB_WHILE_GRABBED";
    var previous = Environment.GetEnvironmentVariable(hint);
    try
    {
        Environment.SetEnvironmentVariable(hint, "0");
        var stream = SessionController.ViewerStartInfo(privateProfile, pairing: false, Path.GetTempPath());
        var pair = SessionController.ViewerStartInfo(privateProfile, pairing: true, Path.GetTempPath());
        Require(stream.Environment[hint] == "1", "An inherited disabled hint must not prevent local Alt+Tab.");
        Require(Environment.GetEnvironmentVariable(hint) == "0", "The launcher environment was changed.");
        Require(pair.Environment[hint] == "0", "Pairing must retain its inherited environment.");
        Require(pair.ArgumentList.SequenceEqual(new[] { "pair", privateProfile.Host }),
            "The pairing launch arguments changed.");
    }
    finally { Environment.SetEnvironmentVariable(hint, previous); }
});

Run("Viewer state keeps identity and disables presence sharing", () =>
{
    const string fixture = "[General]\nidentity=fixture-only\n[streamsettings]\nrichpresence=true\nother=value\n";
    var updated = ViewerState.SetIniValue(fixture, "streamsettings", "richpresence", "false");
    Require(updated.Contains("identity=fixture-only") && updated.Contains("other=value"), "Unrelated settings were lost.");
    Require(updated.Contains("richpresence=false") && !updated.Contains("richpresence=true"), "Presence was not disabled.");
    Require(ViewerState.SetIniValue(updated, "streamsettings", "richpresence", "false") == updated, "Settings update is not idempotent.");
    Require(ViewerState.SetIniValue("", "streamsettings", "richpresence", "false").Contains("[streamsettings]\nrichpresence=false"), "Empty settings were not initialized.");
});

Run("Viewer window size keeps its pixel dimensions at the same DPI", () =>
{
    var size = new ViewerWindowSize(1440, 850, 96);
    var restored = size.FitToWorkingArea(new(100, 70, 900, 600), new(0, 0, 1920, 1040), 96);
    Require(restored == new Rectangle(100, 70, 1440, 850), "The saved window size or current position changed.");
});

Run("Viewer window size scales with monitor DPI", () =>
{
    var size = new ViewerWindowSize(1000, 700, 96);
    var restored = size.FitToWorkingArea(new(100, 50, 900, 600), new(0, 0, 2560, 1400), 144);
    Require(restored == new Rectangle(100, 50, 1500, 1050), "The size did not scale with the display.");
    var scaledDown = new ViewerWindowSize(1500, 1050, 144)
        .FitToWorkingArea(new(100, 50, 900, 600), new(0, 0, 1920, 1040), 96);
    Require(scaledDown.Size == new Size(1000, 700), "The size did not scale down correctly.");
});

Run("Viewer window restore fits smaller and negative-coordinate monitors", () =>
{
    var size = new ViewerWindowSize(2500, 1600, 96);
    Require(size.FitToWorkingArea(new(1800, 1000, 900, 600), new(0, 0, 1280, 720), 96)
        == new Rectangle(0, 0, 1280, 720), "The restored window exceeded the work area.");
    var secondary = new Rectangle(-1920, -100, 1920, 1040);
    var restored = new ViewerWindowSize(1200, 800, 96)
        .FitToWorkingArea(new(-2000, -300, 900, 600), secondary, 96);
    Require(restored == new Rectangle(-1920, -100, 1200, 800), "Negative monitor coordinates were lost.");
    Require(secondary.Contains(restored), "The restored window was off-screen.");
});

Run("Viewer window dimensions reject invalid or unbounded values", () =>
{
    foreach (var size in new[]
    {
        new ViewerWindowSize(0, 600, 96), new ViewerWindowSize(800, -1, 96),
        new ViewerWindowSize(800, 600, 0), new ViewerWindowSize(int.MaxValue, 600, 96),
        new ViewerWindowSize(800, 600, uint.MaxValue)
    }) Require(!size.IsValid, "An invalid window size was accepted.");
});

Run("Viewer window storage is separate, atomic, and tolerant of damaged preferences", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), $"LanView-window-test-{Guid.NewGuid():N}");
    var path = Path.Combine(directory, "viewer-window.json");
    Directory.CreateDirectory(directory);
    try
    {
        Require(ViewerWindowSizeStore.FilePath != ProfileStore.FilePath
            && Path.GetDirectoryName(ViewerWindowSizeStore.FilePath) == Path.GetDirectoryName(ProfileStore.FilePath),
            "Window preferences must not overwrite the connection profile.");
        Require(ViewerWindowSizeStore.Load(path) is null, "A missing preference must use the default size.");
        var first = new ViewerWindowSize(1440, 850, 96);
        ViewerWindowSizeStore.Save(first, path);
        Require(ViewerWindowSizeStore.Load(path) == first, "Window size did not round-trip.");
        var second = first with { Width = 1200 };
        ViewerWindowSizeStore.Save(second, path);
        Require(ViewerWindowSizeStore.Load(path) == second, "A changed size was not saved.");
        try
        {
            ViewerWindowSizeStore.Save(second with { Width = -1 }, path);
            throw new InvalidOperationException("Saving invalid dimensions succeeded.");
        }
        catch (ArgumentException) { }
        Require(ViewerWindowSizeStore.Load(path) == second, "An invalid update overwrote the previous size.");
        Require(Directory.GetFiles(directory).Length == 1, "A temporary preferences file was left behind.");
        foreach (var content in new[] { "{", "null", "{}", "[]", "{\"Width\":-1,\"Height\":850,\"Dpi\":96}", new string(' ', 4097) })
        {
            File.WriteAllText(path, content);
            Require(ViewerWindowSizeStore.Load(path) is null, "A damaged window preference was accepted.");
        }
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
        Directory.Delete(directory);
    }
});

Console.WriteLine($"{passed} passed; {failures} failed. No network connections or remote input were used.");
return failures == 0 ? 0 : 1;

void Run(string name, Action test)
{
    try
    {
        test();
        passed++;
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static string? ValueAfter(IReadOnlyList<string> args, string option)
{
    for (var index = 0; index + 1 < args.Count; index++)
    {
        if (args[index] == option) return args[index + 1];
    }
    return null;
}
