namespace LanView.Windows;

public static class ViewerState
{
    public static string DirectoryPath { get; } = Path.Combine(
        Path.GetDirectoryName(ProfileStore.FilePath)!, "Moonlight");

    public static string Prepare()
    {
        Directory.CreateDirectory(DirectoryPath);
        var marker = Path.Combine(DirectoryPath, "portable.dat");
        if (!File.Exists(marker)) File.WriteAllText(marker, "");

        // Keep the upstream identity outside the application package, including on upgrades.
        var settingsDirectory = Path.Combine(DirectoryPath, "Moonlight Game Streaming Project");
        Directory.CreateDirectory(settingsDirectory);
        var settingsPath = Path.Combine(settingsDirectory, "Moonlight.ini");
        var content = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : "";
        var updated = SetIniValue(content, "streamsettings", "richpresence", "false");
        if (updated != content) File.WriteAllText(settingsPath, updated);
        return DirectoryPath;
    }

    public static string SetIniValue(string content, string section, string key, string value)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
        var header = "[" + section + "]";
        var start = lines.FindIndex(line => line.Trim() == header);
        if (start < 0)
        {
            lines.Add(header);
            lines.Add(key + "=" + value);
        }
        else
        {
            var end = lines.FindIndex(start + 1, line => line.TrimStart().StartsWith('['));
            if (end < 0) end = lines.Count;
            var matching = Enumerable.Range(start + 1, end - start - 1)
                .Where(index => lines[index].TrimStart().StartsWith(key + "=", StringComparison.Ordinal)).ToArray();
            if (matching.Length == 0) lines.Insert(end, key + "=" + value);
            else foreach (var index in matching) lines[index] = key + "=" + value;
        }
        return string.Join("\n", lines).TrimEnd('\n') + "\n";
    }
}
