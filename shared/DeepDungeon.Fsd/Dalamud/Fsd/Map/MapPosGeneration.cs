using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Map
{
    /// <summary>
    /// Runtime generator/loader for Deep Dungeon room centers.
    /// - Persists generated centers to JSON in the plugin config directory.
    /// - Provides runtime centers to consumers (MapPos, UI).
    /// </summary>
    internal static class MapPosGeneration
    {
        private const int MaxRoomsPerFloor = 25;
        private static readonly TimeSpan AutoGenerationRetryInterval = TimeSpan.FromSeconds(5);

        private static readonly object _lock = new();
        private static string _filePath = string.Empty;

        // Persisted centers, loaded from disk and updated at runtime
        // Key format: "<DungeonId>.<FloorsetIndex1Based>|<TilesetIndex>" e.g. "1.1|0"
        private static readonly Dictionary<string, Vector3?[]> _centers = new();

        private static readonly Dictionary<string, DateTime> _lastAutoGenerationAttempt = new();
        private static string _lastKey = string.Empty;
        private static bool _initialized;

        private sealed class PersistFile
        {
            public Dictionary<string, CenterDTO[]> Centers { get; set; } = new();
        }

        private sealed class CenterDTO
        {
            [JsonPropertyName("x")] public float X { get; set; }
            [JsonPropertyName("y")] public float Y { get; set; }
            [JsonPropertyName("z")] public float Z { get; set; }
        }

        private static string ResolveFilePath()
        {
            try
            {
                var baseDir = Service.PluginInterface.GetPluginConfigDirectory();
                Directory.CreateDirectory(baseDir);
                return Path.Combine(baseDir, "DeepDungeonCenters.json");
            }
            catch
            {
                // As a fallback, use current directory
                return "DeepDungeonCenters.json";
            }
        }

        private static unsafe string MakeKey(InstanceContentDeepDungeon* dd)
        {
            uint dungeonId = dd->DeepDungeonId;            // 1=PotD, 2=HoH, 3=EO, 4=PT
            int floorset = dd->Floor / 10 + 1;             // 1..n
            int tileset = dd->ActiveLayoutIndex;           // 0=A, 1=B, 2=Fallacies
            return $"{dungeonId}.{floorset}|{tileset}";
        }

        private static void EnsureInitialized_NoLock()
        {
            if (_initialized)
                return;

            _filePath = ResolveFilePath();
            LoadFromDisk_NoLock();
            _initialized = true;
        }

        public static unsafe void OnEnterDeepDungeon(InstanceContentDeepDungeon* dd)
        {
            // Clear stale debug snapshot from previous floor/layout
            RoomCenterGenerator.ClearDebugSnapshot();
            
            lock (_lock)
            {
                EnsureInitialized_NoLock();
                _lastKey = MakeKey(dd);
            }
        }

        public static void OnExitDeepDungeon()
        {
            lock (_lock)
            {
                SaveToDisk_NoLock();
                _lastAutoGenerationAttempt.Clear();
                _lastKey = string.Empty;
            }
        }

        public static unsafe bool EnsureCentersAvailable(InstanceContentDeepDungeon* dd, bool forceRegenerate = false)
        {
            if (dd == null)
                return false;
            if (dd->ActiveLayoutIndex < 0 || dd->ActiveLayoutIndex > 2)
                return false;
            if (DutyTransitionUtil.IsBetweenAreas())
                return false;

            string key;
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                EnsureInitialized_NoLock();
                key = MakeKey(dd);
                
                // Clear stale debug snapshot if floor/layout changed
                if (key != _lastKey)
                {
                    RoomCenterGenerator.ClearDebugSnapshot();
                    _lastKey = key;
                }
                
                if (!forceRegenerate && _centers.TryGetValue(key, out var arr) && HasAnyCenters(arr))
                    return true;

                if (_lastAutoGenerationAttempt.TryGetValue(key, out var lastAttempt) &&
                    now - lastAttempt < AutoGenerationRetryInterval)
                {
                    return false;
                }

                _lastAutoGenerationAttempt[key] = now;
            }

            if (!TryAutoGenerateCenters(dd, key))
                return false;

            lock (_lock)
            {
                return _centers.TryGetValue(key, out var arr) && HasAnyCenters(arr);
            }
        }

        public static unsafe bool TryGetRuntimeRoomCenter(InstanceContentDeepDungeon* dd, int roomIndex, out Vector3 center)
        {
            center = default;
            if (dd == null || roomIndex < 0 || roomIndex >= MaxRoomsPerFloor)
                return false;
            if (dd->ActiveLayoutIndex < 0 || dd->ActiveLayoutIndex > 2)
                return false;
            lock (_lock)
            {
                var key = MakeKey(dd);
                if (_centers.TryGetValue(key, out var arr))
                {
                    var v = arr[roomIndex];
                    if (v.HasValue)
                    {
                        center = v.Value;
                        return true;
                    }
                }
            }
            return false;
        }

        public static unsafe void OverrideCenter(InstanceContentDeepDungeon* dd, int roomIndex, Vector3 newCenter)
        {
            if (dd == null || roomIndex < 0 || roomIndex >= MaxRoomsPerFloor)
                return;
            if (dd->ActiveLayoutIndex < 0 || dd->ActiveLayoutIndex > 2)
                return;
            lock (_lock)
            {
                var key = MakeKey(dd);
                if (!_centers.TryGetValue(key, out var arr) || arr == null)
                {
                    arr = new Vector3?[MaxRoomsPerFloor];
                    _centers[key] = arr;
                }
                arr[roomIndex] = newCenter;
                SaveToDisk_NoLock();
            }
        }

        public static unsafe bool ClearFloorCenters(InstanceContentDeepDungeon* dd)
        {
            if (dd == null)
                return false;
            if (dd->ActiveLayoutIndex < 0 || dd->ActiveLayoutIndex > 2)
                return false;
            lock (_lock)
            {
                var key = MakeKey(dd);
                bool removed = _centers.Remove(key);
                if (removed)
                {
                    SaveToDisk_NoLock();
                    try
                    {
                        Service.Log.Info($"[MapPosGeneration] Cleared saved room centers for {key}");
                    }
                    catch
                    {
                        // logging failure is non-fatal
                    }
                }
                return removed;
            }
        }

        public static void ClearAllCenters()
        {
            lock (_lock)
            {
                bool hadCenters = _centers.Count > 0;
                _centers.Clear();
                SaveToDisk_NoLock();
                if (hadCenters)
                {
                    try
                    {
                        Service.Log.Info("[MapPosGeneration] Cleared all saved room centers.");
                    }
                    catch
                    {
                        // logging failure is non-fatal
                    }
                }
            }
        }

        public static unsafe bool ApplyGeneratedCenters(InstanceContentDeepDungeon* dd, Vector3?[] centers)
        {
            if (dd == null || centers == null || centers.Length != MaxRoomsPerFloor)
                return false;
            if (dd->ActiveLayoutIndex < 0 || dd->ActiveLayoutIndex > 2)
                return false;

            lock (_lock)
            {
                var key = MakeKey(dd);
                var clone = new Vector3?[MaxRoomsPerFloor];
                for (int i = 0; i < MaxRoomsPerFloor; i++)
                    clone[i] = centers[i];
                _centers[key] = clone;

                SaveToDisk_NoLock();
                try
                {
                    Service.Log.Info($"[MapPosGeneration] Applied generated room centers for {key}");
                }
                catch
                {
                    // ignore logging failures
                }
            }

            return true;
        }

        private static void LoadFromDisk_NoLock()
        {
            try
            {
                if (string.IsNullOrEmpty(_filePath))
                    _filePath = ResolveFilePath();
                if (!File.Exists(_filePath))
                    return;

                var json = File.ReadAllText(_filePath);
                var data = JsonSerializer.Deserialize<PersistFile>(json);
                if (data == null || data.Centers == null)
                    return;

                _centers.Clear();
                int skippedLegacy = 0;
                foreach (var kv in data.Centers)
                {
                    var arr = new Vector3?[MaxRoomsPerFloor];
                    var src = kv.Value;
                    bool hasNonZeroY = false;
                    for (int i = 0; i < MaxRoomsPerFloor && i < src.Length; i++)
                    {
                        if (src[i] != null)
                        {
                            arr[i] = new Vector3(src[i].X, src[i].Y, src[i].Z);
                            if (src[i].Y != 0f) hasNonZeroY = true;
                        }
                    }
                    if (!hasNonZeroY && arr.Any(c => c.HasValue))
                    {
                        skippedLegacy++;
                        continue;
                    }
                    _centers[kv.Key] = arr;
                }
                if (skippedLegacy > 0)
                    Service.Log.Info($"[MapPosGeneration] Discarded {skippedLegacy} legacy tilesets (missing Y data)");
                Service.Log.Info($"[MapPosGeneration] Loaded centers: {_centers.Count} tilesets from {_filePath}");
            }
            catch (Exception ex)
            {
                Service.Log.Error($"[MapPosGeneration] Load failed: {ex}");
            }
        }

        private static void SaveToDisk_NoLock()
        {
            try
            {
                if (string.IsNullOrEmpty(_filePath))
                    _filePath = ResolveFilePath();
                var pf = new PersistFile();
                foreach (var kv in _centers)
                {
                    var src = kv.Value;
                    var arr = new CenterDTO[MaxRoomsPerFloor];
                    for (int i = 0; i < MaxRoomsPerFloor; i++)
                    {
                        if (src[i].HasValue)
                            arr[i] = new CenterDTO { X = src[i]!.Value.X, Y = src[i]!.Value.Y, Z = src[i]!.Value.Z };
                        else
                            arr[i] = null!;
                    }
                    pf.Centers[kv.Key] = arr;
                }

                var json = JsonSerializer.Serialize(pf, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                Service.Log.Error($"[MapPosGeneration] Save failed: {ex}");
            }
        }

        private static bool HasAnyCenters(Vector3?[]? arr)
        {
            if (arr == null || arr.Length == 0)
                return false;
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i].HasValue)
                    return true;
            }
            return false;
        }

        private static unsafe bool TryAutoGenerateCenters(InstanceContentDeepDungeon* dd, string key)
        {
            try
            {
                if (!RoomCenterGenerator.TryGenerate(dd, out var centers, out var stats, out var error))
                {
                    var reason = string.IsNullOrEmpty(error) ? "unknown error" : error;
                    try { Service.Log.Warning($"[MapPosGeneration] Auto-generation failed for {key}: {reason}"); } catch { }
                    return false;
                }

                return ApplyGeneratedCenters(dd, centers);
            }
            catch (Exception ex)
            {
                try { Service.Log.Error($"[MapPosGeneration] Auto-generation error for {key}: {ex}"); } catch { }
                return false;
            }
        }

    }
}


