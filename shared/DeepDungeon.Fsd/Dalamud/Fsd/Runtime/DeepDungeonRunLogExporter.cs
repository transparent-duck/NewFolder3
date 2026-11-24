using System.IO.Compression;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using Lumina.Excel.Sheets;
using ReplayHeader = FFXIVClientStructs.FFXIV.Client.Game.ContentsReplayManager.FFXIVREPLAYHeader;

namespace DeepDungeon.Fsd.Dalamud.Runtime;

internal static class DeepDungeonRunLogExporter
{
    private static readonly TimeSpan RecentWindow = TimeSpan.FromDays(3);
    private const uint DeepDungeonContentType = 21;
    private const int ReplayBodyOffset = 0x36c;

    public static unsafe string ExportRecentToDesktop(string hostIdentity)
    {
        string desktopDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.DesktopDirectory);
        var framework = Framework.Instance();
        string? replayDirectory = framework != null && framework->UserPathString is { Length: > 0 } userPath
            ? Path.Combine(userPath, "replay")
            : null;
        return ExportRecent(
            DeepDungeonRunRecorder.GetLogDirectory(),
            desktopDirectory,
            hostIdentity,
            DateTime.UtcNow,
            replayDirectory,
            contentId => Service.DataManager.GetExcelSheet<ContentFinderCondition>()?
                .GetRowOrDefault(contentId)?.ContentType.RowId == DeepDungeonContentType);
    }

    internal static string ExportRecent(
        string logDirectory,
        string destinationDirectory,
        string hostIdentity,
        DateTime nowUtc,
        string? replayDirectory,
        Predicate<ushort> isDeepDungeon)
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

            if (replayDirectory != null && ReadLatestReplay(replayDirectory, isDeepDungeon) is { } replay)
            {
                var entry = archive.CreateEntry($"ARR/{replay.Name}", CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                entryStream.Write(replay.Data);
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

    private static (string Name, byte[] Data)? ReadLatestReplay(
        string replayDirectory, Predicate<ushort> isDeepDungeon)
    {
        var candidates = new List<(string Path, uint Timestamp)>();
        foreach (string directory in new[] { replayDirectory, Path.Combine(replayDirectory, "autorenamed") })
        {
            try
            {
                foreach (string path in Directory.EnumerateFiles(directory, "*.dat"))
                {
                    try
                    {
                        using var input = OpenReplay(path);
                        if (ReadReplayHeader(input) is { } header && isDeepDungeon(header.ContentFinderConditionId))
                            candidates.Add((path, header.Timestamp));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // The client or replay archiver may be writing or moving this file.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The optional archive folder may not exist or be readable.
            }
        }

        foreach (var candidate in candidates.OrderByDescending(candidate => candidate.Timestamp))
        {
            try
            {
                using var input = OpenReplay(candidate.Path);
                if (ReadReplayHeader(input) is not { } header || header.Timestamp != candidate.Timestamp ||
                    !isDeepDungeon(header.ContentFinderConditionId))
                    continue;
                // Read the whole recording before creating its ZIP entry so a source read failure
                // cannot leave a partial ARR in the export.
                input.Position = 0;
                var data = new byte[checked((int)input.Length)];
                input.ReadExactly(data);
                return (Path.GetFileName(candidate.Path), data);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
            {
                // Try the next newest recording.
            }
        }

        return null;
    }

    private static FileStream OpenReplay(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    private static unsafe ReplayHeader? ReadReplayHeader(FileStream input)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ReplayHeader)];
        input.ReadExactly(bytes);
        if (!bytes.StartsWith("FFXIVREPLAY\0"u8))
            return null;

        var header = MemoryMarshal.Read<ReplayHeader>(bytes);
        return header.ReplayLength > 0 && input.Length == ReplayBodyOffset + (long)header.ReplayLength
            ? header
            : null;
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
