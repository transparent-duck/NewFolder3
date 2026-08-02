using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DeepDungeon.Fsd.Dalamud.Runtime
{
	internal sealed class DeepDungeonRunRecorder : IDisposable
	{
		internal const int MaxRetainedRunLogFiles = 10;

		private readonly string _filePath;
		private readonly StreamWriter _writer;
		private readonly JsonSerializerOptions _jsonOptions = new()
		{
			WriteIndented = false
		};
		private bool _disposed;

		public DeepDungeonRunRecorder(string sessionName)
		{
			var logDir = GetLogDirectory();
			Directory.CreateDirectory(logDir);

			string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
			string safeSessionName = SanitizeFileSegment(sessionName);
			_filePath = Path.Combine(logDir, $"{timestamp}-{safeSessionName}.jsonl");
			_writer = new StreamWriter(_filePath, append: false, new UTF8Encoding(false))
			{
				AutoFlush = true
			};
			RetainApplicableRunLogFiles(logDir, _filePath);
		}

		public string FilePath => _filePath;

		public static string GetLogDirectory()
		{
			var baseDir = Service.PluginInterface.GetPluginConfigDirectory();
			return Path.Combine(baseDir, "DeepDungeonRuns");
		}

		internal static void RetainApplicableRunLogFiles(string logDirectory, string activeFilePath)
		{
			string[] paths = Directory.GetFiles(logDirectory, "*.jsonl");
			if (paths.Length <= MaxRetainedRunLogFiles)
				return;

			string activeFullPath = Path.GetFullPath(activeFilePath);
			var entries = new FileInfo[paths.Length];
			for (int i = 0; i < paths.Length; i++)
				entries[i] = new FileInfo(paths[i]);

			Array.Sort(entries, CompareNewestFirst);

			var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				activeFullPath
			};

			for (int i = 0; i < entries.Length && keep.Count < MaxRetainedRunLogFiles; i++)
				keep.Add(entries[i].FullName);

			for (int i = 0; i < entries.Length; i++)
			{
				if (!keep.Contains(entries[i].FullName))
					entries[i].Delete();
			}
		}

		private static int CompareNewestFirst(FileInfo left, FileInfo right)
		{
			int byTime = right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc);
			if (byTime != 0)
				return byTime;
			return string.CompareOrdinal(right.Name, left.Name);
		}

		public void Record(string eventType, object? data)
		{
			if (_disposed)
				return;

			var envelope = new
			{
				timestampUtc = DateTime.UtcNow,
				eventType,
				data
			};

			string json = JsonSerializer.Serialize(envelope, _jsonOptions);
			_writer.WriteLine(json);
		}

		public void Dispose()
		{
			if (_disposed)
				return;

			_disposed = true;
			try { _writer.Dispose(); } catch { }
		}

		private static string SanitizeFileSegment(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
				return "run";

			var invalid = Path.GetInvalidFileNameChars();
			var chars = value.ToCharArray();
			for (int i = 0; i < chars.Length; i++)
			{
				if (Array.IndexOf(invalid, chars[i]) >= 0)
				{
					chars[i] = '_';
				}
			}

			return new string(chars);
		}
	}
}
