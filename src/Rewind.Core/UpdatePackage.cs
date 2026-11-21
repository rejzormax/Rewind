using System.IO.Compression;

namespace Rewind.Core;

public static class UpdatePackage
{
    public const long MaximumExpandedBytes = 512L * 1024 * 1024;

    public static void Extract(string archivePath, string destination)
    {
        var root = Path.GetFullPath(destination);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("The update destination is not empty.");
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is 0 or > 1500) throw new InvalidDataException("The update archive has too many entries.");
        var files = archive.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')).ToArray();
        if (files.Length == 0) throw new InvalidDataException("The update archive is empty.");
        var names = files.Select(e => ValidateParts(e.FullName)).ToArray();
        var prefix = names.All(p => p.Length >= 2 && p[0] == names[0][0]) ? names[0][0] : null;
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<(ZipArchiveEntry Entry, string Target)>();
        long expanded = 0;
        for (var index = 0; index < files.Length; index++)
        {
            var entry = files[index];
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("Symbolic links are not allowed in updates.");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExpandedBytes) throw new InvalidDataException("The update archive expands beyond its limit.");
            var parts = prefix == null ? names[index] : names[index][1..];
            var target = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
            if (!IsWithin(target, root) || !targets.Add(target)) throw new InvalidDataException("An archive entry escaped its destination or duplicates another entry.");
            plans.Add((entry, target));
        }
        if (!plans.Any(p => string.Equals(Path.GetRelativePath(root, p.Target), "Rewind.exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The release archive does not contain Rewind.exe.");
        Directory.CreateDirectory(root);
        var buffer = new byte[128 * 1024];
        foreach (var (entry, target) in plans)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            long copied = 0;
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                copied += count;
                if (copied > entry.Length) throw new InvalidDataException("An archive entry expanded beyond its declared size.");
                output.Write(buffer, 0, count);
            }
            if (copied != entry.Length) throw new InvalidDataException("An archive entry is incomplete.");
        }
    }

    public static bool IsWithin(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string[] ValidateParts(string name)
    {
        var normalized = name.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':') || normalized.Contains('\0')) throw new InvalidDataException("An archive entry has an absolute or invalid path.");
        var parts = normalized.Split('/');
        if (parts.Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("An archive entry has an unsafe path.");
        foreach (var part in parts)
        {
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9')
                throw new InvalidDataException("Device names are not allowed in updates.");
        }
        return parts;
    }
}
