using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using System.Reflection;
using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud.Map
{
	public static class PalacePalData
	{
		private const string LocalFileName = "DeepDungeonPalacePal.json";
		private const string ImportReportFilePrefix = "DeepDungeonPalacePalImport";
		private static readonly object _lock = new();
		private static bool _initialized;
		private static string _localFilePath = string.Empty;

		// cache of what we have saved locally (by territory)
		private static readonly Dictionary<ushort, List<Marker>> _localByTerritory = new();
		private static readonly Dictionary<ushort, int> _validCountByTerritory = new();

		// Last fetch status for UI
		private static ushort _lastTerritory;
		private static bool _lastFetchSucceeded;

		private sealed class Marker
		{
			[JsonPropertyName("t")] public string Type { get; set; } = "Trap";
			[JsonPropertyName("x")] public float X { get; set; }
			[JsonPropertyName("y")] public float Y { get; set; }
			[JsonPropertyName("z")] public float Z { get; set; }
		}

		private sealed class LocalCache
		{
			[JsonPropertyName("territories")] public Dictionary<ushort, List<Marker>> Territories { get; set; } = new();
			[JsonPropertyName("savedAtUtc")] public DateTime SavedAtUtc { get; set; }
		}

		// PalacePal legacy json (per territory) minimal reader
		private sealed class PalLegacySave
		{
			[JsonPropertyName("Version")] public int Version { get; set; }
			[JsonPropertyName("Markers")] public HashSet<PalLegacyMarker> Markers { get; set; } = new();
		}
		private sealed class PalLegacyMarker
		{
			[JsonPropertyName("Type")] public int Type { get; set; }
			[JsonPropertyName("Position")] public Vector3 Position { get; set; }
		}

		private static void EnsureInitialized_NoLock()
		{
			if (_initialized)
				return;

			try
			{
				var baseDir = Service.PluginInterface.GetPluginConfigDirectory();
				Directory.CreateDirectory(baseDir);
				_localFilePath = Path.Combine(baseDir, LocalFileName);

				if (File.Exists(_localFilePath))
				{
					var json = File.ReadAllText(_localFilePath);
					var cache = JsonSerializer.Deserialize<LocalCache>(json);
					if (cache?.Territories != null)
					{
						_localByTerritory.Clear();
						_validCountByTerritory.Clear();
						foreach (var kv in cache.Territories)
						{
							List<Marker> markers = kv.Value ?? new List<Marker>();
							_localByTerritory[kv.Key] = markers;
							_validCountByTerritory[kv.Key] = CountValidMarkers(kv.Key, markers);
						}
					}
				}
			}
			catch (Exception ex)
			{
				Service.Log.Error($"[PalacePalData] Init failed: {ex}");
			}

			_initialized = true;
		}

		public static void OnEnterDeepDungeon()
		{
			lock (_lock)
			{
				EnsureInitialized_NoLock();
				var terr = (ushort)Service.ClientState.TerritoryType;
				_lastTerritory = terr;
				_lastFetchSucceeded = false;

				if (TryFetchFromPalacePal_NoLock(terr, out var markers))
				{
					_localByTerritory[terr] = markers;
					_validCountByTerritory[terr] = CountValidMarkers(terr, markers);
					SaveLocal_NoLock();
					_lastFetchSucceeded = _validCountByTerritory[terr] > 0;
				}
				else if (TryFetchFromPalacePalSQLite_NoLock(terr, out markers, out var importReport))
				{
					SaveImportReport_NoLock(importReport);
					_localByTerritory[terr] = markers;
					_validCountByTerritory[terr] = importReport.ValidMarkerCount;
					SaveLocal_NoLock();
					_lastFetchSucceeded = importReport.ValidMarkerCount > 0;
					if (importReport.QuarantinedMarkerCount > 0)
					{
						Service.Log.Warning(
							$"[PalacePalData] Territory {terr}: imported {importReport.SourceMarkerCount} source markers; " +
							$"{importReport.ValidMarkerCount} valid and {importReport.QuarantinedMarkerCount} explicitly quarantined. " +
							$"See {ImportReportFilePrefix}-{terr}.json.");
					}
				}
				else
				{
					_lastFetchSucceeded = false;
				}
			}
		}

		private static string GetPalacePalConfigDir()
		{
			var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

			// Prefer CN client path without space ("PalacePal"), then fall back to variants
			var cnNoSpace = Path.Combine(appData, "XIVLauncherCN", "pluginConfigs", "PalacePal");
			if (Directory.Exists(cnNoSpace))
				return cnNoSpace;

			var cnSpace = Path.Combine(appData, "XIVLauncherCN", "pluginConfigs", "Palace Pal");
			if (Directory.Exists(cnSpace))
				return cnSpace;

			var stdNoSpace = Path.Combine(appData, "XIVLauncher", "pluginConfigs", "PalacePal");
			if (Directory.Exists(stdNoSpace))
				return stdNoSpace;

			var stdSpace = Path.Combine(appData, "XIVLauncher", "pluginConfigs", "Palace Pal");
			if (Directory.Exists(stdSpace))
				return stdSpace;

			// Default to CN no-space path
			return cnNoSpace;
		}

		private static bool TryFetchFromPalacePal_NoLock(ushort territoryId, out List<Marker> markers)
		{
			markers = new List<Marker>();
			try
			{
				// Attempt legacy JSON first (no extra dependencies required)
				var palConfigDir = GetPalacePalConfigDir();
				var legacyPath = Path.Combine(palConfigDir, $"{territoryId}.json");
				if (!File.Exists(legacyPath))
					return false;

				var content = File.ReadAllText(legacyPath);
				if (string.IsNullOrWhiteSpace(content))
					return false;

				// v1 legacy format: root is an array of markers; else, it is Save object with Markers
				List<PalLegacyMarker> legacyMarkers;
				if (content[0] == '[')
				{
					legacyMarkers = JsonSerializer.Deserialize<List<PalLegacyMarker>>(content) ?? new List<PalLegacyMarker>();
				}
				else
				{
					var save = JsonSerializer.Deserialize<PalLegacySave>(content);
					legacyMarkers = save?.Markers != null ? new List<PalLegacyMarker>(save.Markers) : new List<PalLegacyMarker>();
				}

				foreach (var lm in legacyMarkers)
				{
					// 1 = Trap, 2 = Hoard (match PalacePal legacy)
					if (lm.Type != 1 && lm.Type != 2)
						continue;
					markers.Add(new Marker
					{
						Type = lm.Type == 1 ? "Trap" : "Hoard",
						X = lm.Position.X,
						Y = lm.Position.Y,
						Z = lm.Position.Z
					});
				}

				return markers.Count > 0;
			}
			catch (Exception ex)
			{
				Service.Log.Error($"[PalacePalData] Fetch from PalacePal failed: {ex}");
				return false;
			}
		}

		private static bool TryFetchFromPalacePalSQLite_NoLock(
			ushort territoryId,
			out List<Marker> markers,
			out PalacePalTerritoryImportReport importReport)
		{
			markers = new List<Marker>();
			importReport = new PalacePalTerritoryImportReport();
			var palConfigDir = GetPalacePalConfigDir();
			var dbPath = Path.Combine(palConfigDir, "palace-pal.data.sqlite3");
			if (!File.Exists(dbPath))
				return false;

			// Use the copy already loaded by PalacePal; the host does not add a SQLite dependency.
			var sqliteAsm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Microsoft.Data.Sqlite");
			if (sqliteAsm == null)
			{
				try { sqliteAsm = Assembly.Load("Microsoft.Data.Sqlite"); } catch { /* not available */ }
			}
			if (sqliteAsm == null)
				return false;

			var connType = sqliteAsm.GetType("Microsoft.Data.Sqlite.SqliteConnection")
			               ?? throw new InvalidDataException("Loaded Microsoft.Data.Sqlite has no SqliteConnection type.");
			var cs = $"Data Source={dbPath};Mode=ReadOnly;Cache=Shared";
			using var conn = (IDisposable)(Activator.CreateInstance(connType, cs)
			                              ?? throw new InvalidDataException("Could not create PalacePal SQLite connection."));
			connType.GetMethod("Open")!.Invoke(conn, null);
			ValidateSqliteSchema(connType, conn);

			var sourceRecords = new List<PalacePalSourceMarkerRecord>();
			using IDisposable cmd = CreateCommand(
				connType,
				conn,
				"SELECT LocalId, Type, X, Y, Z, Seen, Source, SinceVersion " +
				$"FROM Locations WHERE TerritoryType = {territoryId} ORDER BY LocalId");
			object rdr = ExecuteReader(cmd);
			using var rdrDisposable = (IDisposable)rdr;
			Type rdrType = rdr.GetType();
			MethodInfo readM = rdrType.GetMethod("Read")!;
			MethodInfo getInt32 = rdrType.GetMethod("GetInt32", [typeof(int)])!;
			MethodInfo getDouble = rdrType.GetMethod("GetDouble", [typeof(int)])!;
			MethodInfo getString = rdrType.GetMethod("GetString", [typeof(int)])!;

			while ((bool)readM.Invoke(rdr, null)!)
			{
				int localId = (int)getInt32.Invoke(rdr, [0])!;
				int typeVal = (int)getInt32.Invoke(rdr, [1])!;
				if (typeVal is not (1 or 2))
					throw new InvalidDataException($"PalacePal Locations LocalId {localId} has unsupported Type {typeVal}.");

				float x = ReadExactSingle(getDouble, rdr, 2, localId, "X");
				float y = ReadExactSingle(getDouble, rdr, 3, localId, "Y");
				float z = ReadExactSingle(getDouble, rdr, 4, localId, "Z");
				int seen = (int)getInt32.Invoke(rdr, [5])!;
				if (seen is not (0 or 1))
					throw new InvalidDataException($"PalacePal Locations LocalId {localId} has unsupported Seen value {seen}.");
				int source = (int)getInt32.Invoke(rdr, [6])!;
				string sinceVersion = (string)getString.Invoke(rdr, [7])!;
				string? quarantineReason = PalacePalImportPolicy.GetQuarantineReason(territoryId, x, y, z);

				markers.Add(new Marker
				{
					Type = typeVal == 1 ? "Trap" : "Hoard",
					X = x,
					Y = y,
					Z = z
				});
				sourceRecords.Add(new PalacePalSourceMarkerRecord
				{
					LocalId = localId,
					Type = typeVal,
					X = x,
					Y = y,
					Z = z,
					Seen = seen == 1,
					Source = source,
					SinceVersion = sinceVersion,
					QuarantineReason = quarantineReason
				});
			}

			importReport = PalacePalTerritoryImportReport.Create(
				Path.GetFileName(dbPath),
				territoryId,
				sourceRecords,
				DateTime.UtcNow);
			return markers.Count > 0;
		}

		private static void ValidateSqliteSchema(Type connType, IDisposable conn)
		{
			var columns = new List<PalacePalSqliteColumn>();
			using IDisposable cmd = CreateCommand(connType, conn, "PRAGMA table_info(Locations)");
			object rdr = ExecuteReader(cmd);
			using var rdrDisposable = (IDisposable)rdr;
			Type rdrType = rdr.GetType();
			MethodInfo readM = rdrType.GetMethod("Read")!;
			MethodInfo getInt32 = rdrType.GetMethod("GetInt32", [typeof(int)])!;
			MethodInfo getString = rdrType.GetMethod("GetString", [typeof(int)])!;
			while ((bool)readM.Invoke(rdr, null)!)
			{
				columns.Add(new PalacePalSqliteColumn(
					(int)getInt32.Invoke(rdr, [0])!,
					(string)getString.Invoke(rdr, [1])!,
					(string)getString.Invoke(rdr, [2])!,
					(int)getInt32.Invoke(rdr, [3])! != 0,
					(int)getInt32.Invoke(rdr, [5])! != 0));
			}

			PalacePalImportPolicy.ValidateSqliteSchema(columns);
		}

		private static IDisposable CreateCommand(Type connType, IDisposable conn, string commandText)
		{
			object cmd = connType.GetMethod("CreateCommand")!.Invoke(conn, null)
			             ?? throw new InvalidDataException("Could not create PalacePal SQLite command.");
			cmd.GetType().GetProperty("CommandText")!.SetValue(cmd, commandText);
			return (IDisposable)cmd;
		}

		private static object ExecuteReader(IDisposable cmd)
		{
			MethodInfo executeReader = cmd.GetType().GetMethod("ExecuteReader", Type.EmptyTypes)
			                           ?? throw new InvalidDataException("PalacePal SQLite command has no ExecuteReader method.");
			return executeReader.Invoke(cmd, null)
			       ?? throw new InvalidDataException("PalacePal SQLite command returned no reader.");
		}

		private static float ReadExactSingle(
			MethodInfo getDouble,
			object reader,
			int ordinal,
			int localId,
			string coordinate)
		{
			double raw = (double)getDouble.Invoke(reader, [ordinal])!;
			float value = (float)raw;
			if ((double)value != raw)
			{
				throw new InvalidDataException(
					$"PalacePal Locations LocalId {localId} coordinate {coordinate} cannot be represented " +
					"exactly by PalacePal's declared single-precision coordinate contract.");
			}

			return value;
		}

		public static List<Vector3> GetTrapPositionsCurrentTerritory()
		{
			return GetPositionsCurrentTerritory(includeTraps: true, includeHoards: false);
		}

		public static List<Vector3> GetCandidatePositionsCurrentTerritory()
		{
			return GetPositionsCurrentTerritory(includeTraps: true, includeHoards: true);
		}

		private static List<Vector3> GetPositionsCurrentTerritory(bool includeTraps, bool includeHoards)
		{
			lock (_lock)
			{
				var result = new List<Vector3>();
				if (_localByTerritory.TryGetValue(_lastTerritory, out var list))
				{
					for (int i = 0; i < list.Count; i++)
					{
						var m = list[i];
						if (m.Type == "Trap" && includeTraps ||
						    m.Type == "Hoard" && includeHoards)
						{
							if (PalacePalImportPolicy.GetQuarantineReason(_lastTerritory, m.X, m.Y, m.Z) != null)
								continue;
							result.Add(new Vector3(m.X, m.Y, m.Z));
						}
					}
				}
				return result;
			}
		}

		private static int CountValidMarkers(ushort territoryId, IReadOnlyList<Marker> markers)
		{
			int count = 0;
			for (int i = 0; i < markers.Count; i++)
			{
				Marker marker = markers[i];
				if (PalacePalImportPolicy.GetQuarantineReason(territoryId, marker.X, marker.Y, marker.Z) == null)
					count++;
			}

			return count;
		}

		private static void SaveImportReport_NoLock(PalacePalTerritoryImportReport report)
		{
			string baseDir = Service.PluginInterface.GetPluginConfigDirectory();
			string path = Path.Combine(baseDir, $"{ImportReportFilePrefix}-{report.TerritoryId}.json");
			var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
			{
				WriteIndented = true,
				PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
				DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
			});
			File.WriteAllText(path, json);
		}

		private static void SaveLocal_NoLock()
		{
			try
			{
				var cache = new LocalCache
				{
					Territories = new Dictionary<ushort, List<Marker>>(_localByTerritory),
					SavedAtUtc = DateTime.UtcNow
				};
				var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions
				{
					WriteIndented = true,
					DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
				});
				File.WriteAllText(_localFilePath, json);
			}
			catch (Exception ex)
			{
				Service.Log.Error($"[PalacePalData] Save local failed: {ex}");
			}
		}

		public static bool LastFetchFailedForCurrentTerritory
		{
			get
			{
				lock (_lock)
				{
					return !_lastFetchSucceeded && _lastTerritory != 0;
				}
			}
		}

		public static bool HasLocalCacheForCurrentTerritory
		{
			get
			{
				lock (_lock)
				{
					if (!_initialized)
						EnsureInitialized_NoLock();
					return _validCountByTerritory.TryGetValue(_lastTerritory, out int validCount) &&
					       validCount > 0;
				}
			}
		}
	}
}


