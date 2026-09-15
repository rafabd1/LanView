using System.Text;
using LanView.Windows;
using LanView.Windows.ClipboardIntegration;

var passed = 0;
var failed = 0;
var temporary = Path.Combine(Path.GetTempPath(), "lanview-clipboard-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
void Check(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name); }
}
void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
void Reject(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is InvalidDataException or FormatException) { return; }
    throw new Exception("Expected rejection");
}

try
{
    Check("ordinary Unicode paths", () => ClipboardFilePolicy.ValidateRelativePath("Pasta/Olá 世界.txt"));
    Check("UTF8 component limit matches Linux", () =>
    {
        ClipboardFilePolicy.ValidateRelativePath(new string('界', 85));
        Reject(() => ClipboardFilePolicy.ValidateRelativePath(new string('界', 86)));
    });
    Check("UNC paths reject without touching filesystem", () =>
    {
        Reject(() => ClipboardFilePolicy.ValidateLocalSource(@"\\192.0.2.1\share\file.txt"));
        Reject(() => ClipboardFilePolicy.ValidateLocalSource(@"\\?\UNC\192.0.2.1\share\file.txt"));
        Reject(() => ClipboardFilePolicy.ValidateLocalSource(@"\\?\C:\file.txt"));
    });
    foreach (var path in new[] { "../escape", "/absolute", "C:/drive", "a\\b", "a//b", "a/./b", "a/../b", "file.", "file ", "CON", "con.txt", "NUL", "COM1.txt", "LPT².log", "a:b", "a\nb" })
        Check("reject unsafe or reserved path " + path.Replace('\n', '_'), () => Reject(() => ClipboardFilePolicy.ValidateRelativePath(path)));
    Check("strict protocol ids", () =>
    {
        Assert(ClipboardFilePolicy.IsValidId("offer_123-ABC"));
        Assert(!ClipboardFilePolicy.IsValidId("../offer"));
        Assert(!ClipboardFilePolicy.IsValidId(new string('a', 129)));
    });
    Check("directory and nested empty file", () =>
    {
        var result = ClipboardFilePolicy.ValidateManifest([new("folder", 0, true), new("folder/empty", 0, false)]);
        Assert(result.Count == 2);
    });
    Check("reject case-insensitive collision", () => Reject(() => ClipboardFilePolicy.ValidateManifest([new("A", 0, false), new("a", 0, false)])));
    Check("reject missing parent", () => Reject(() => ClipboardFilePolicy.ValidateManifest([new("dir/file", 0, false)])));
    Check("reject file used as directory", () => Reject(() => ClipboardFilePolicy.ValidateManifest([new("dir", 0, false), new("dir/file", 0, false)])));
    Check("reject oversized total", () => Reject(() => ClipboardFilePolicy.ValidateManifest([new("a", ClipboardFilePolicy.MaxTotalBytes, false), new("b", 1, false)])));
    Check("reject negative size", () => Reject(() => ClipboardFilePolicy.ValidateManifest([new("a", -1, false)])));
    Check("reject directory with content size", () => Reject(() => ClipboardFilePolicy.ValidateManifest([new("a", 1, true)])));
    Check("reject more than 4096 items", () => Reject(() => ClipboardFilePolicy.ValidateManifest(Enumerable.Range(0, 4097).Select(index => new ClipboardFileEntry("f" + index, 0, false)))));
    Check("accept exact 2 GiB", () => ClipboardFilePolicy.ValidateManifest([new("a", ClipboardFilePolicy.MaxTotalBytes, false)]));
    Check("resolved path stays beneath cache", () => Assert(ClipboardFilePolicy.ResolveUnderRoot(temporary, "folder/name").StartsWith(temporary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
    Check("strict SSH command and host verification", () =>
    {
        var info = ClipboardBridge.CreateSshStartInfo(new("192.168.50.10", "desktop", ""));
        Assert(info.ArgumentList.Last() == "~/.local/bin/lanview-bridge");
        Assert(info.ArgumentList.Contains("StrictHostKeyChecking=yes"));
        Assert(info.ArgumentList.Contains("BatchMode=yes"));
        Assert(!info.UseShellExecute && info.CreateNoWindow);
    });
    Check("recursive enumeration preserves root names", () =>
    {
        var source = Path.Combine(temporary, "source");
        Directory.CreateDirectory(Path.Combine(source, "folder"));
        File.WriteAllText(Path.Combine(source, "folder", "file.txt"), "example");
        var result = ClipboardFilePolicy.EnumerateSelection([source]);
        Assert(result.Select(entry => entry.Entry.Path).SequenceEqual(["source", "source/folder", "source/folder/file.txt"]));
    });
    Check("two files with the same root name reject", () =>
    {
        var first = Path.Combine(temporary, "one");
        var second = Path.Combine(temporary, "two");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "a.txt"), "1");
        File.WriteAllText(Path.Combine(second, "A.txt"), "2");
        Reject(() => ClipboardFilePolicy.EnumerateSelection([Path.Combine(first, "a.txt"), Path.Combine(second, "A.txt")]));
    });
    Check("multi-chunk download and retained complete cache", () =>
    {
        var bytes = Enumerable.Range(0, 180_000).Select(value => (byte)(value % 251)).ToArray();
        var location = Path.Combine(temporary, "cache-good");
        string[] paths;
        using (var cache = new DownloadCache("roundtrip", [new("root", 0, true), new("root/file.bin", bytes.Length, false), new("root/empty", 0, false)], location))
        {
            for (var offset = 0; offset < bytes.Length; offset += ClipboardFilePolicy.MaxChunkBytes)
            {
                var count = Math.Min(ClipboardFilePolicy.MaxChunkBytes, bytes.Length - offset);
                cache.WriteChunk("root/file.bin", offset, Convert.ToBase64String(bytes, offset, count));
            }
            paths = cache.Commit();
        }
        Assert(paths.Length == 1 && Directory.Exists(paths[0]));
        Assert(File.ReadAllBytes(Path.Combine(paths[0], "file.bin")).SequenceEqual(bytes));
        Assert(new FileInfo(Path.Combine(paths[0], "empty")).Length == 0);
    });
    Check("incomplete cache is discarded", () =>
    {
        var location = Path.Combine(temporary, "cache-incomplete");
        using (var cache = new DownloadCache("incomplete", [new("file", 2, false)], location))
        {
            cache.WriteChunk("file", 0, Convert.ToBase64String([1]));
            Reject(() => cache.Commit());
        }
        Assert(!Directory.EnumerateFileSystemEntries(location).Any());
    });
    Check("reject wrong offset and unknown path", () =>
    {
        using var cache = new DownloadCache("offset", [new("file", 2, false)], Path.Combine(temporary, "cache-offset"));
        Reject(() => cache.WriteChunk("file", 1, "AQ=="));
        Reject(() => cache.WriteChunk("other", 0, "AQ=="));
        Reject(() => cache.WriteChunk("../outside", 0, "AQ=="));
    });
    Check("reject file length overrun and invalid base64", () =>
    {
        using var cache = new DownloadCache("length", [new("file", 1, false)], Path.Combine(temporary, "cache-length"));
        Reject(() => cache.WriteChunk("file", 0, "AQI="));
        Reject(() => cache.WriteChunk("file", 0, "!"));
    });
    Check("reject oversized chunk", () =>
    {
        using var cache = new DownloadCache("chunk", [new("file", 100_000, false)], Path.Combine(temporary, "cache-chunk"));
        Reject(() => cache.WriteChunk("file", 0, Convert.ToBase64String(new byte[65_537])));
    });
    Check("JSON line parsing preserves UTF8 text and CRLF", () =>
    {
        async Task Test()
        {
            var lines = new List<string>();
            await foreach (var line in ClipboardBridge.ReadProtocolLinesAsync(new StringReader("{\"text\":\"Olá 世界\"}\r\n{}\n"))) lines.Add(line);
            Assert(lines.SequenceEqual(["{\"text\":\"Olá 世界\"}", "{}"]));
        }
        Test().GetAwaiter().GetResult();
    });
    Check("reject unterminated and oversized protocol lines", () =>
    {
        async Task Consume(string text)
        {
            await foreach (var line in ClipboardBridge.ReadProtocolLinesAsync(new StringReader(text))) { }
        }
        Reject(() => Consume("{}").GetAwaiter().GetResult());
        Reject(() => Consume(new string('x', 8 * 1024 * 1024 + 1) + "\n").GetAwaiter().GetResult());
    });
}
finally
{
    // This exact random directory was created by this test process; it contains no user data.
    Directory.Delete(temporary, recursive: true);
}
Console.WriteLine($"{passed} passed; {failed} failed. Clipboard and network were not accessed.");
return failed == 0 ? 0 : 1;
