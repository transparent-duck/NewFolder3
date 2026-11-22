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
	public static class PalantirData
	{
		private const string ImportReportFilePrefix = "DeepDungeonPalantirImport";
		private static readonly object _lock = new();

		// in-memory markers loaded for this session (by territory)
		private static readonly Dictionary<ushort, List<Marker>> _markersByTerritory = new();
		private static readonly Dictionary<ushort, int> _validCountByTerritory = new();

		// Last fetch status for UI
		private static ushort _lastTerritory;
		private static bool _lastFetchSucceeded;

		private sealed class Marker
		{
			public string Type { get; } = "Trap";
			public float X { get; }
			public float Y { get; }
			public float Z { get; }

			public Marker(string type, float x, float y, float z)
			{
				Type = type;
				X = x;
				Y = y;
				Z = z;
			}
		}

		public static void OnEnterDeepDungeon()
		{
			lock (_lock)
			{
				var terr = (ushort)Service.ClientState.TerritoryType;
				_lastTerritory = terr;
				_lastFetchSucceeded = false;

				if (TryFetchFromPalantir_NoLock(terr, out var markers, out var importReport))
				{
					SaveImportReport_NoLock(importReport);
					_markersByTerritory[terr] = markers;
					_validCountByTerritory[terr] = importReport.ValidMarkerCount;
					_lastFetchSucceeded = importReport.ValidMarkerCount > 0;
					if (importReport.QuarantinedMarkerCount > 0)
					{
						Service.Log.Warning(
							$"[PalantirData] Territory {terr}: imported {importReport.SourceMarkerCount} source markers; " +
							$"{importReport.ValidMarkerCount} valid and {importReport.QuarantinedMarkerCount} explicitly quarantined. " +
							$"See {ImportReportFilePrefix}-{terr}.json.");
					}
				}
			}
		}

		private static string GetPalantirDbPath()
		{
			// Palantir's config directory is a sibling of ours: both live under the
			// same launcher's pluginConfigs, named after the plugin internal name.
			var ownConfigDir = Service.PluginInterface.GetPluginConfigDirectory();
			return Path.GetFullPath(
				Path.Combine(ownConfigDir, "..", "Palantir", "palantir.db"));
		}

		private static bool TryFetchFromPalantir_NoLock(
			ushort territoryId,
			out List<Marker> markers,
			out PalantirTerritoryImportReport importReport)
		{
			markers = new List<Marker>();
			importReport = new PalantirTerritoryImportReport();
			var dbPath = GetPalantirDbPath();
			if (!File.Exists(dbPath))
				return false;

			// Use the copy already loaded by Palantir; the host does not add a SQLite dependency.
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
			                              ?? throw new InvalidDataException("Could not create Palantir SQLite connection."));
			connType.GetMethod("Open")!.Invoke(conn, null);
			ValidateSqliteSchema(connType, conn);

			var sourceRecords = new List<PalantirSourceMarkerRecord>();
			using IDisposable cmd = CreateCommand(
				connType,
				conn,
				"SELECT Id, Type, X, Y, Z, Confirmations, Local, Synced " +
				$"FROM Markers WHERE Territory = {territoryId} ORDER BY Id");
			object rdr = ExecuteReader(cmd);
			using var rdrDisposable = (IDisposable)rdr;
			Type rdrType = rdr.GetType();
			MethodInfo readM = rdrType.GetMethod("Read")!;
			MethodInfo getInt32 = rdrType.GetMethod("GetInt32", [typeof(int)])!;
			MethodInfo getDouble = rdrType.GetMethod("GetDouble", [typeof(int)])!;
			MethodInfo getString = rdrType.GetMethod("GetString", [typeof(int)])!;

			while ((bool)readM.Invoke(rdr, null)!)
			{
				string id = (string)getString.Invoke(rdr, [0])!;
				int typeVal = (int)getInt32.Invoke(rdr, [1])!;
				float x = ReadExactSingle(getDouble, rdr, 2, id, "X");
				float y = ReadExactSingle(getDouble, rdr, 3, id, "Y");
				float z = ReadExactSingle(getDouble, rdr, 4, id, "Z");
				int confirmations = (int)getInt32.Invoke(rdr, [5])!;
				int local = (int)getInt32.Invoke(rdr, [6])!;
				int synced = (int)getInt32.Invoke(rdr, [7])!;
				if (local is not (0 or 1) || synced is not (0 or 1))
					throw new InvalidDataException(
						$"Palantir Markers Id {id} has unsupported Local/Synced values {local}/{synced}.");

				// Type 3 (Debug) and any future non-trap/hoard marker are not candidates;
				// keep them in the report but out of the position universe.
				string? quarantineReason = typeVal is (1 or 2)
					? PalantirImportPolicy.GetQuarantineReason(territoryId, x, y, z)
					: "unsupportedMarkerType";

				if (quarantineReason == null)
				{
					markers.Add(new Marker(
						typeVal == 1 ? "Trap" : "Hoard",
						x,
						y,
						z));
				}
				sourceRecords.Add(new PalantirSourceMarkerRecord
				{
					Id = id,
					Type = typeVal,
					X = x,
					Y = y,
					Z = z,
					Confirmations = confirmations,
					Local = local == 1,
					Synced = synced == 1,
					QuarantineReason = quarantineReason
				});
			}

			importReport = PalantirTerritoryImportReport.Create(
				Path.GetFileName(dbPath),
				territoryId,
				sourceRecords,
				DateTime.UtcNow);
			return markers.Count > 0;
		}

		private static void ValidateSqliteSchema(Type connType, IDisposable conn)
		{
			var columns = new List<PalantirSqliteColumn>();
			using IDisposable cmd = CreateCommand(connType, conn, "PRAGMA table_info(Markers)");
			object rdr = ExecuteReader(cmd);
			using var rdrDisposable = (IDisposable)rdr;
			Type rdrType = rdr.GetType();
			MethodInfo readM = rdrType.GetMethod("Read")!;
			MethodInfo getInt32 = rdrType.GetMethod("GetInt32", [typeof(int)])!;
			MethodInfo getString = rdrType.GetMethod("GetString", [typeof(int)])!;
			while ((bool)readM.Invoke(rdr, null)!)
			{
				columns.Add(new PalantirSqliteColumn(
					(int)getInt32.Invoke(rdr, [0])!,
					(string)getString.Invoke(rdr, [1])!,
					(string)getString.Invoke(rdr, [2])!,
					(int)getInt32.Invoke(rdr, [3])! != 0,
					(int)getInt32.Invoke(rdr, [5])! != 0));
			}

			PalantirImportPolicy.ValidateSqliteSchema(columns);
		}

		private static IDisposable CreateCommand(Type connType, IDisposable conn, string commandText)
		{
			object cmd = connType.GetMethod("CreateCommand")!.Invoke(conn, null)
			             ?? throw new InvalidDataException("Could not create Palantir SQLite command.");
			cmd.GetType().GetProperty("CommandText")!.SetValue(cmd, commandText);
			return (IDisposable)cmd;
		}

		private static object ExecuteReader(IDisposable cmd)
		{
			MethodInfo executeReader = cmd.GetType().GetMethod("ExecuteReader", Type.EmptyTypes)
			                           ?? throw new InvalidDataException("Palantir SQLite command has no ExecuteReader method.");
			return executeReader.Invoke(cmd, null)
			       ?? throw new InvalidDataException("Palantir SQLite command returned no reader.");
		}

		private static float ReadExactSingle(
			MethodInfo getDouble,
			object reader,
			int ordinal,
			string id,
			string coordinate)
		{
			double raw = (double)getDouble.Invoke(reader, [ordinal])!;
			float value = (float)raw;
			if ((double)value != raw)
			{
				throw new InvalidDataException(
					$"Palantir Markers Id {id} coordinate {coordinate} cannot be represented " +
					"exactly by Palantir's declared single-precision coordinate contract.");
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
				if (_markersByTerritory.TryGetValue(_lastTerritory, out var list))
				{
					for (int i = 0; i < list.Count; i++)
					{
						var m = list[i];
						if (m.Type == "Trap" && includeTraps ||
						    m.Type == "Hoard" && includeHoards)
						{
							if (PalantirImportPolicy.GetQuarantineReason(_lastTerritory, m.X, m.Y, m.Z) != null)
								continue;
							result.Add(new Vector3(m.X, m.Y, m.Z));
						}
					}
				}
				return result;
			}
		}

		private static void SaveImportReport_NoLock(PalantirTerritoryImportReport report)
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

		public static bool HasMarkersForCurrentTerritory
		{
			get
			{
				lock (_lock)
				{
					return _validCountByTerritory.TryGetValue(_lastTerritory, out int validCount) &&
					       validCount > 0;
				}
			}
		}
	}
}
