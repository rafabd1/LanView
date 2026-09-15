using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace LanView.Windows.ClipboardIntegration;

public sealed record ClipboardFileEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("directory")] bool Directory);

public sealed record ClipboardLocalFile(ClipboardFileEntry Entry, string Source);

public static class ClipboardFilePolicy
{
    public const int MaxEntries = 4096;
    public const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxChunkBytes = 64 * 1024;
    public const int MaxTextBytes = 1024 * 1024;
    private static readonly char[] InvalidCharacters = ['<', '>', ':', '"', '|', '?', '*', '\\'];

    public static bool IsValidId(string? id) => id is { Length: > 0 and <= 128 }
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || Encoding.UTF8.GetByteCount(path) > 1024 || path.StartsWith('/')
            || path.EndsWith('/') || path.Any(c => c < 32 || c == 127)
            || path.IndexOfAny(InvalidCharacters) >= 0)
            throw new InvalidDataException("Nome de arquivo incompatível com o Windows.");
        foreach (var component in path.Split('/'))
        {
            if (component is "" or "." or ".." || Encoding.UTF8.GetByteCount(component) > 255
                || component.EndsWith('.') || component.EndsWith(' '))
                throw new InvalidDataException("Caminho de arquivo inválido.");
            var stem = component.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT"))
                && (stem[3] is >= '0' and <= '9' or '¹' or '²' or '³'))
                throw new InvalidDataException("Nome de arquivo reservado no Windows.");
        }
    }

    public static IReadOnlyList<ClipboardFileEntry> ValidateManifest(IEnumerable<ClipboardFileEntry> source)
    {
        var entries = source.Take(MaxEntries + 1).ToArray();
        if (entries.Length is 0 or > MaxEntries)
            throw new InvalidDataException("A seleção deve ter entre 1 e 4096 arquivos e pastas.");
        var paths = new Dictionary<string, ClipboardFileEntry>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in entries)
        {
            ValidateRelativePath(entry.Path);
            if (entry.Size < 0 || entry.Directory && entry.Size != 0 || entry.Size > MaxTotalBytes - total)
                throw new InvalidDataException("A seleção ultrapassa 2 GiB ou contém tamanho inválido.");
            total += entry.Size;
            if (!paths.TryAdd(entry.Path, entry))
                throw new InvalidDataException("Há nomes de arquivo que coincidem no Windows.");
        }
        foreach (var entry in entries)
        {
            var slash = entry.Path.LastIndexOf('/');
            if (slash >= 0 && (!paths.TryGetValue(entry.Path[..slash], out var parent) || !parent.Directory))
                throw new InvalidDataException("A árvore de arquivos está incompleta.");
        }
        return entries;
    }

    public static IReadOnlyList<ClipboardLocalFile> EnumerateSelection(IEnumerable<string> selected, CancellationToken cancellation = default)
    {
        var result = new List<ClipboardLocalFile>();
        void Add(string absolute, string relative)
        {
            cancellation.ThrowIfCancellationRequested();
            if (result.Count >= MaxEntries) throw new InvalidDataException("A seleção ultrapassa 4096 arquivos e pastas.");
            ValidateRelativePath(relative);
            EnsureNoReparsePoints(absolute);
            var attributes = File.GetAttributes(absolute);
            var directory = attributes.HasFlag(FileAttributes.Directory);
            result.Add(new(new(relative, directory ? 0 : new FileInfo(absolute).Length, directory), absolute));
            if (directory)
                foreach (var child in System.IO.Directory.EnumerateFileSystemEntries(absolute))
                    Add(child, relative + "/" + System.IO.Path.GetFileName(child));
        }
        foreach (var item in selected)
        {
            ValidateLocalSource(item);
            var full = System.IO.Path.GetFullPath(item).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            Add(full, System.IO.Path.GetFileName(full));
        }
        ValidateManifest(result.Select(item => item.Entry));
        return result;
    }

    public static void ValidateLocalSource(string path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.Path.IsPathFullyQualified(path))
            throw new InvalidDataException("Seleção de arquivo inválida.");
        var root = System.IO.Path.GetPathRoot(path);
        // Reject network and device namespace paths before any filesystem lookup.
        if (root is not { Length: 3 } || root[1] != ':' || path.StartsWith('\\')
            || new DriveInfo(root).DriveType == DriveType.Network)
            throw new InvalidDataException("Copie arquivos de uma unidade local; compartilhamentos de rede não são transferidos.");
    }

    public static string ResolveUnderRoot(string root, string relative)
    {
        ValidateRelativePath(relative);
        var fullRoot = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Caminho fora do cache.");
        return full;
    }

    public static void EnsureNoReparsePoints(string path)
    {
        var ancestors = new Stack<string>();
        for (var current = System.IO.Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = System.IO.Path.GetDirectoryName(current))
            ancestors.Push(current);
        // Check ancestors first; inspecting a child before its parent could follow a junction.
        foreach (var current in ancestors)
        {
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Links e pontos de redirecionamento não são transferidos.");
        }
    }

    public static string FingerprintText(string text) => "text:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string FingerprintPaths(IEnumerable<string> paths) => "files:" + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join('\0', paths.Select(System.IO.Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase)))));
}
