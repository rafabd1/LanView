using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LanView.Windows;

public sealed record Profile(string Host, string User, string MoonlightPath, bool ShareClipboard = true)
{
    public static Profile Empty { get; } = new("", "", "");

    public Profile Normalize() => new(
        (Host ?? "").Trim(),
        (User ?? "").Trim(),
        (MoonlightPath ?? "").Trim(), ShareClipboard);

    public string? Validate(bool requireMoonlight = true)
    {
        if (!IPAddress.TryParse(Host, out var address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || address.ToString() != Host)
        {
            return "Informe o endereço IPv4 local do Linux.";
        }

        var bytes = address.GetAddressBytes();
        var isPrivate = bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
        if (!isPrivate)
        {
            return "Use um endereço da rede local: 10.x.x.x, 172.16–31.x.x ou 192.168.x.x.";
        }

        if (!Regex.IsMatch(User, @"\A[a-zA-Z_][a-zA-Z0-9_.-]{0,63}\$?\z", RegexOptions.CultureInvariant))
        {
            return "Informe um usuário SSH válido, sem espaços ou caracteres de comando.";
        }

        if (!requireMoonlight)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(MoonlightPath)
            || !Path.IsPathFullyQualified(MoonlightPath)
            || !string.Equals(Path.GetExtension(MoonlightPath), ".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(MoonlightPath))
        {
            return "Componente de vídeo não encontrado. Reinstale o LanView ou extraia o pacote completo.";
        }

        return null;
    }
}

public sealed record SessionStatus(string State, string GpuState, string Detail);

public static class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "LanView", "profile.json");

    private static string LegacyFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LanView", "profile.json");

    public static string BundledMoonlightPath { get; } = Path.Combine(
        AppContext.BaseDirectory, "tools", "Moonlight", "Moonlight.exe");

    public static string ResolveMoonlightPath(string fallbackPath, string? applicationDirectory = null)
    {
        var bundledPath = applicationDirectory is null ? BundledMoonlightPath
            : Path.Combine(applicationDirectory, "tools", "Moonlight", "Moonlight.exe");
        return File.Exists(bundledPath) ? bundledPath : fallbackPath;
    }

    private static Profile ResolveBundledViewer(Profile profile) =>
        profile with { MoonlightPath = ResolveMoonlightPath(profile.MoonlightPath) };

    public static Profile Load()
    {
        var loadPath = File.Exists(FilePath) ? FilePath : LegacyFilePath;
        if (!File.Exists(loadPath))
        {
            return ResolveBundledViewer(Profile.Empty);
        }

        return ResolveBundledViewer((JsonSerializer.Deserialize<Profile>(File.ReadAllText(loadPath), JsonOptions)
            ?? throw new InvalidDataException("O perfil salvo está vazio."))
            .Normalize());
    }

    public static void Save(Profile profile)
    {
        var normalized = profile.Normalize();
        if (normalized.Validate(requireMoonlight: false) is { } error)
        {
            throw new ArgumentException(error, nameof(profile));
        }

        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"profile-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(normalized, JsonOptions));
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
