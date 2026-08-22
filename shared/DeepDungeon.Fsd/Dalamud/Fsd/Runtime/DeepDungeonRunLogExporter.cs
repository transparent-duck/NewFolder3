using System.IO.Compression;

namespace DeepDungeon.Fsd.Dalamud.Runtime;

internal static class DeepDungeonRunLogExporter
{
    private static readonly TimeSpan RecentWindow = TimeSpan.FromDays(3);

    public static string ExportRecentToDesktop(string hostIdentity)
    {
        string desktopDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.DesktopDirectory);
        return ExportRecent(
            DeepDungeonRunRecorder.GetLogDirectory(),
            desktopDirectory,
            hostIdentity,
            DateTime.UtcNow);
    }

    internal static string ExportRecent(
        string logDirectory,
        string destinationDirectory,
        string hostIdentity,
        DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostIdentity);

        if (!Directory.Exists(logDirectory))
            throw new InvalidOperationException("Deep Dungeon run-log directory does not exist.");
        if (!Directory.Exists(destinationDirectory))
            throw new InvalidOperationException("Desktop directory does not exist.");

        DateTime cutoffUtc = nowUtc.ToUniversalTime() - RecentWindow;
        string[] sourcePaths = Directory
            .EnumerateFiles(logDirectory, "*.jsonl", SearchOption.TopDirectoryOnly)
            .Where(path => File.GetLastWriteTimeUtc(path) >= cutoffUtc)
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();
        if (sourcePaths.Length == 0)
            throw new InvalidOperationException("No Deep Dungeon run logs from the last three days were found.");

        string hostName = hostIdentity.Split('.', 2)[0];
        string archiveName =
            $"{SanitizeFileSegment(hostName)}-運行日誌-{DateTime.Now:yyyyMMdd-HHmmssfff}.zip";
        string archivePath = Path.Combine(destinationDirectory, archiveName);

        try
        {
            using var output = new FileStream(
                archivePath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create);
            foreach (string sourcePath in sourcePaths)
            {
                ZipArchiveEntry entry = archive.CreateEntry(
                    Path.GetFileName(sourcePath),
                    CompressionLevel.Optimal);
                using Stream entryStream = entry.Open();
                using var input = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                input.CopyTo(entryStream);
            }

            return archivePath;
        }
        catch
        {
            if (File.Exists(archivePath))
                File.Delete(archivePath);
            throw;
        }
    }

    private static string SanitizeFileSegment(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        char[] chars = value.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }

        return new string(chars);
    }
}
