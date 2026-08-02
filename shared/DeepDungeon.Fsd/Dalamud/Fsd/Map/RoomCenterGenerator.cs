using System;
using System.Collections.Generic;
using System.Numerics;
using DeepDungeon.Fsd.Dalamud;
using DeepDungeon.Fsd.Dalamud.GameState;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Map
{
    /// <summary>
    /// Generates per-room centers for the current deep-dungeon layout by sampling BG collision respawn walls.
    /// </summary>
    internal static unsafe class RoomCenterGenerator
    {
        private const int MaxRoomsPerFloor = 25;
        private const float WithinRoomLinkDistance = 7f;
        private const float IsolatedRoomNeighborDistance = 120f;
        private const int MinRespawnWallsPerRoom = 6;
        private const int LayoutSplitThreshold = 80;
        private const float ValidationTolerance = 40f;

        // Threshold: if room center count >= this, use 5x5; otherwise use 4x3
        private const int Grid5x5MinRoomCount = 17;

        /// <summary>
        /// Configuration for a specific grid layout.
        /// Room indices always use 5x5-based indexing (0-24) for compatibility with GetLocalPlayerRoomIndex.
        /// </summary>
        internal readonly struct GridConfig
        {
            public int Cols { get; }
            public int Rows { get; }
            public int RowOffset { get; }  // Starting row in 5x5 grid (0 for 5x5, 1 for 3x4)
            public int ColOffset { get; }  // Starting col in 5x5 grid (0 for both)
            public int RoomCount => Cols * Rows;
            public int[] ValidRoomIndices { get; }  // All valid room indices in 5x5 terms

            public GridConfig(int cols, int rows, int rowOffset, int colOffset, int[] validRoomIndices)
            {
                Cols = cols;
                Rows = rows;
                RowOffset = rowOffset;
                ColOffset = colOffset;
                ValidRoomIndices = validRoomIndices;
            }

            /// <summary>
            /// Convert grid-local (row, col) to 5x5-based room index.
            /// </summary>
            public int RoomIndex(int gridRow, int gridCol) => (gridRow + RowOffset) * 5 + (gridCol + ColOffset);

            /// <summary>
            /// Convert 5x5-based room index to grid-local (row, col). Returns (-1,-1) if not in grid.
            /// </summary>
            public (int row, int col) GridPosition(int roomIndex5x5)
            {
                int row5x5 = roomIndex5x5 / 5;
                int col5x5 = roomIndex5x5 % 5;
                int gridRow = row5x5 - RowOffset;
                int gridCol = col5x5 - ColOffset;
                if (gridRow < 0 || gridRow >= Rows || gridCol < 0 || gridCol >= Cols)
                    return (-1, -1);
                return (gridRow, gridCol);
            }

            /// <summary>
            /// Check if a 5x5-based room index is valid for this grid.
            /// </summary>
            public bool IsValidRoomIndex(int roomIndex5x5)
            {
                var (r, c) = GridPosition(roomIndex5x5);
                return r >= 0 && c >= 0;
            }
        }

        // Standard 5x5 grid (25 rooms)
        // Rows 0-4, Cols 0-4, no offset
        internal static readonly GridConfig Grid5x5 = new(
            cols: 5, rows: 5, rowOffset: 0, colOffset: 0,
            validRoomIndices: new[] { 0,1,2,3,4, 5,6,7,8,9, 10,11,12,13,14, 15,16,17,18,19, 20,21,22,23,24 });

        // Large-room 4x3 grid (12 rooms)
        // Uses 5x5 indices: rows 1-3, cols 0-3 -> rooms 5,6,7,8, 10,11,12,13, 15,16,17,18
        internal static readonly GridConfig Grid4x3 = new(
            cols: 4, rows: 3, rowOffset: 1, colOffset: 0,
            validRoomIndices: new[] { 5,6,7,8, 10,11,12,13, 15,16,17,18 });

        /// <summary>
        /// Detects the appropriate grid configuration by trying both grids and picking the best fit.
        /// </summary>
        private static GridConfig DetectGridConfig(List<Vector2> roomCenters)
        {
            int count = roomCenters.Count;
            
            // Quick heuristics first
            if (count <= 8)
                return Grid4x3;  // Too few for 5x5
            if (count >= 20)
                return Grid5x5;  // Clearly 5x5
            
            // For ambiguous cases (9-19 rooms), try both grids and compare fit
            float cost5x5 = CalculateGridFitCost(roomCenters, Grid5x5);
            float cost4x3 = CalculateGridFitCost(roomCenters, Grid4x3);
            
            // Prefer the grid with lower fit cost
            // Add a small bias toward 5x5 since it's more common
            var chosen = cost4x3 < cost5x5 * 0.9f ? Grid4x3 : Grid5x5;
            
            try { Service.Log.Info($"[RoomCenterGenerator] Grid detection: {count} centers, 5x5 cost={cost5x5:F1}, 4x3 cost={cost4x3:F1} -> {chosen.Cols}x{chosen.Rows}"); } catch { }
            return chosen;
        }

        /// <summary>
        /// Calculates how well room centers fit a grid pattern.
        /// Lower cost = better fit. Uses the spacing regularity of room centers.
        /// </summary>
        private static float CalculateGridFitCost(List<Vector2> roomCenters, GridConfig grid)
        {
            if (roomCenters.Count < grid.Rows || roomCenters.Count < grid.Cols)
                return float.MaxValue;

            // Extract X and Z coordinates
            var xCoords = new float[roomCenters.Count];
            var zCoords = new float[roomCenters.Count];
            for (int i = 0; i < roomCenters.Count; i++)
            {
                xCoords[i] = roomCenters[i].X;
                zCoords[i] = roomCenters[i].Y;  // Note: Vector2.Y is Z in world space
            }

            // Run 1D k-means on each axis
            var xClusters = RunAxisKMeans(xCoords, grid.Cols);
            var zClusters = RunAxisKMeans(zCoords, grid.Rows);

            if (xClusters == null || zClusters == null)
                return float.MaxValue;

            // Calculate within-cluster variance (lower = more regular spacing)
            float xVariance = CalculateClusterVariance(xCoords, xClusters);
            float zVariance = CalculateClusterVariance(zCoords, zClusters);

            // Calculate spacing regularity (are clusters evenly spaced?)
            float xSpacingIrregularity = CalculateSpacingIrregularity(xClusters.Centers);
            float zSpacingIrregularity = CalculateSpacingIrregularity(zClusters.Centers);

            // Combined cost: variance + spacing irregularity
            float cost = xVariance + zVariance + xSpacingIrregularity + zSpacingIrregularity;

            // Penalty if expected room count doesn't match
            int expectedRooms = grid.RoomCount;
            int actualRooms = roomCenters.Count;
            float roomCountPenalty = MathF.Abs(expectedRooms - actualRooms) * 5f;
            
            return cost + roomCountPenalty;
        }

        private static float CalculateClusterVariance(float[] values, AxisClusterResult clusters)
        {
            float totalVariance = 0f;
            var counts = new int[clusters.ClusterCount];
            var sums = new float[clusters.ClusterCount];
            var sumSqs = new float[clusters.ClusterCount];

            for (int i = 0; i < values.Length; i++)
            {
                int c = clusters.Assignments[i];
                counts[c]++;
                sums[c] += values[i];
                sumSqs[c] += values[i] * values[i];
            }

            for (int c = 0; c < clusters.ClusterCount; c++)
            {
                if (counts[c] > 1)
                {
                    float mean = sums[c] / counts[c];
                    float variance = (sumSqs[c] / counts[c]) - (mean * mean);
                    totalVariance += variance * counts[c];
                }
            }

            return totalVariance / values.Length;
        }

        private static float CalculateSpacingIrregularity(float[] sortedCenters)
        {
            if (sortedCenters.Length < 2)
                return 0f;

            // Calculate spacings between consecutive cluster centers
            var spacings = new float[sortedCenters.Length - 1];
            for (int i = 0; i < spacings.Length; i++)
                spacings[i] = sortedCenters[i + 1] - sortedCenters[i];

            // Calculate mean and variance of spacings
            float meanSpacing = 0f;
            for (int i = 0; i < spacings.Length; i++)
                meanSpacing += spacings[i];
            meanSpacing /= spacings.Length;

            float variance = 0f;
            for (int i = 0; i < spacings.Length; i++)
            {
                float diff = spacings[i] - meanSpacing;
                variance += diff * diff;
            }
            variance /= spacings.Length;

            // Return coefficient of variation (normalized irregularity)
            return meanSpacing > 0 ? MathF.Sqrt(variance) / meanSpacing * 10f : 0f;
        }

        internal readonly struct GenerationStats
        {
            public GenerationStats(int respawnPoints, int roomClusters, int appliedRooms, int fallbackRooms)
            {
                RespawnPoints = respawnPoints;
                RoomClusters = roomClusters;
                AppliedRooms = appliedRooms;
                FallbackRooms = fallbackRooms;
            }

            public int RespawnPoints { get; }
            public int RoomClusters { get; }
            public int AppliedRooms { get; }
            public int FallbackRooms { get; }
        }

        internal sealed class DebugSnapshot
        {
            public float PlayerY { get; set; }
            public List<Vector3> RawRespawnWalls { get; } = new();
            public List<Vector3> ActiveLayoutWalls { get; } = new();
            public List<Vector3> RoomCenters { get; } = new();
            public Vector3?[]? FinalCenters { get; set; }
            public bool[]? ActualCentersMask { get; set; }
            public int PlayerRoomIndex { get; set; } = -1;
            public Vector2 PlayerXZ { get; set; }
            public Vector2? PlayerRoomCenter { get; set; }
            public Vector2 ColumnBasis { get; set; }
            public Vector2 RowBasis { get; set; }
            public float[]? ColumnCoords { get; set; }
            public float[]? RowCoords { get; set; }
            public Vector2 MeanPoint { get; set; }
            public Vector2?[]? PredictedCenters { get; set; }
            public int FailureRoomIndex { get; set; } = -1;
            public Vector2? FailureActualCenter { get; set; }
            public Vector2? FailurePredictedCenter { get; set; }
            public bool AlignmentFailed { get; set; }
            public string Error { get; set; } = string.Empty;

            // Grid detection info
            public int DetectedGridCols { get; set; } = 5;
            public int DetectedGridRows { get; set; } = 5;
            public int DetectedRoomCenterCount { get; set; }
            public int LayoutIndex { get; set; } = -1;
            public int Floor { get; set; }

            // Layout separation info
            public int LayoutSeparationK { get; set; } = 2;
            public int RawRespawnWallCount { get; set; }
            public int ActiveLayoutWallCount { get; set; }
        }

        private static DebugSnapshot? _latestDebugSnapshot;

        public static DebugSnapshot? GetDebugSnapshot() => _latestDebugSnapshot;

        /// <summary>
        /// Clears the cached debug snapshot. Should be called when entering a new floor.
        /// </summary>
        public static void ClearDebugSnapshot()
        {
            _latestDebugSnapshot = null;
        }

        private sealed class AxisClusterResult
        {
            public AxisClusterResult(int count, int[] assignments, float[] centers)
            {
                ClusterCount = count;
                Assignments = assignments;
                Centers = centers;
            }

            public int ClusterCount { get; }
            public int[] Assignments { get; }
            public float[] Centers { get; private set; }

            public void Reverse()
            {
                int max = ClusterCount - 1;
                for (int i = 0; i < Assignments.Length; i++)
                    Assignments[i] = max - Assignments[i];
                Array.Reverse(Centers);
            }
        }

        private sealed class RespawnCluster
        {
            public RespawnCluster(Vector3 center, List<int> members)
            {
                Center = center;
                Members = members;
            }

            public Vector3 Center { get; }
            public List<int> Members { get; }
        }

        public static bool TryGenerate(InstanceContentDeepDungeon* dd, out Vector3?[] centers, out GenerationStats stats, out string error)
        {
            centers = Array.Empty<Vector3?>();
            stats = default;
            error = string.Empty;

            if (dd == null)
            {
                error = "DeepDungeon director unavailable.";
                return false;
            }

            var player = Service.LocalPlayer;
            if (player == null)
            {
                error = "Local player unavailable.";
                return false;
            }

            int playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
            if (playerRoom < 0 || playerRoom >= MaxRoomsPerFloor)
            {
                error = "Unable to resolve player's room index.";
                return false;
            }

            var debug = new DebugSnapshot
            {
                PlayerY = player.Position.Y,
                PlayerRoomIndex = playerRoom,
                PlayerXZ = new Vector2(player.Position.X, player.Position.Z),
                Floor = dd->Floor,
                LayoutIndex = dd->ActiveLayoutIndex
            };

            var rawPoints = BgCollisionShapeFinder.GetFilteredCenters();
            if (rawPoints.Count == 0)
            {
                debug.Error = "No respawn walls detected in BG collision.";
                _latestDebugSnapshot = debug;
                error = "No respawn walls detected in BG collision.";
                return false;
            }
            for (int i = 0; i < rawPoints.Count; i++)
                debug.RawRespawnWalls.Add(rawPoints[i].Center);
            debug.RawRespawnWallCount = rawPoints.Count;

            var (activeLayout, layoutSepK) = SelectActiveLayout(rawPoints, player.Position);
            debug.LayoutSeparationK = layoutSepK;
            
            for (int pass = 0; pass < 2; pass++)
            {
                if (!RemoveIsolatedRespawnWalls(activeLayout))
                    break;
            }
            for (int i = 0; i < activeLayout.Count; i++)
                debug.ActiveLayoutWalls.Add(activeLayout[i].Center);
            debug.ActiveLayoutWallCount = activeLayout.Count;

            if (activeLayout.Count < LayoutSplitThreshold / 2)
            {
                debug.Error = "Insufficient respawn walls available for clustering.";
                _latestDebugSnapshot = debug;
                error = "Insufficient respawn walls available for clustering.";
                return false;
            }

            var roomCenters3D = CollapseRespawnWalls(activeLayout);
            var roomCenters = new List<Vector2>(roomCenters3D.Count);
            for (int i = 0; i < roomCenters3D.Count; i++)
                roomCenters.Add(new Vector2(roomCenters3D[i].X, roomCenters3D[i].Z));
            
            // Detect grid configuration by trying both grids and comparing fit
            var grid = DetectGridConfig(roomCenters);
            int minClusters = Math.Min(grid.Cols, grid.Rows);

            // Store grid detection info in debug snapshot
            debug.DetectedGridCols = grid.Cols;
            debug.DetectedGridRows = grid.Rows;
            debug.DetectedRoomCenterCount = roomCenters.Count;
            
            if (roomCenters.Count < minClusters)
            {
                debug.Error = $"Not enough room clusters detected (found {roomCenters.Count}, need at least {minClusters} for {grid.Cols}x{grid.Rows} grid).";
                _latestDebugSnapshot = debug;
                error = debug.Error;
                return false;
            }
            foreach (var rc in roomCenters3D)
                debug.RoomCenters.Add(rc);

            // Validate playerRoom is within detected grid bounds (using 5x5-based indices)
            if (!grid.IsValidRoomIndex(playerRoom))
            {
                debug.Error = $"Player room index {playerRoom} not valid for detected {grid.Cols}x{grid.Rows} grid (valid: {string.Join(",", grid.ValidRoomIndices)}).";
                _latestDebugSnapshot = debug;
                error = debug.Error;
                return false;
            }

            var mean = ComputeMean(roomCenters);
            var axisPrimary = new Vector2(1f, 0f);
            var axisSecondary = new Vector2(0f, 1f);
            bool primaryIsColumn = true;

            var axisCoordsPrimary = new float[roomCenters.Count];
            var axisCoordsSecondary = new float[roomCenters.Count];
            for (int i = 0; i < roomCenters.Count; i++)
            {
                var relative = roomCenters[i] - mean;
                axisCoordsPrimary[i] = relative.X;
                axisCoordsSecondary[i] = relative.Y;
            }

            int playerCenterIdx = FindNearestIndex(roomCenters, new Vector2(player.Position.X, player.Position.Z));

            // Run k-means with grid-specific K values (cols for X axis, rows for Z axis)
            var primaryClusters = RunAxisKMeans(axisCoordsPrimary, grid.Cols);
            var secondaryClusters = RunAxisKMeans(axisCoordsSecondary, grid.Rows);

            if (primaryClusters == null || secondaryClusters == null)
            {
                debug.Error = "Failed to cluster room centers into grid axes.";
                _latestDebugSnapshot = debug;
                error = "Failed to cluster room centers into grid axes.";
                return false;
            }

            if (!TryAlignAxes(grid, playerRoom, playerCenterIdx, primaryClusters, secondaryClusters, axisPrimary, axisSecondary, primaryIsColumn,
                    out var columnAssignments, out var rowAssignments,
                    out var columnCenters, out var rowCenters, out var columnVector, out var rowVector))
            {
                try
                {
                    int primaryAssign = primaryClusters.Assignments[playerCenterIdx];
                    int secondaryAssign = secondaryClusters.Assignments[playerCenterIdx];
                    Service.Log.Info($"[RoomCenterGenerator] Axis alignment failed (playerRoom={playerRoom}, playerCenterIdx={playerCenterIdx}, primaryAssign={primaryAssign}, secondaryAssign={secondaryAssign}, grid={grid.Cols}x{grid.Rows}).");
                }
                catch
                {
                    // ignore logging errors
                }
                debug.AlignmentFailed = true;
                debug.Error = "Unable to align generated axes with room grid.";
                _latestDebugSnapshot = debug;
                error = "Unable to align generated axes with room grid.";
                return false;
            }

            var result = new Vector3?[MaxRoomsPerFloor];
            var actualMask = new bool[MaxRoomsPerFloor];
            var predictedCenters = new Vector2?[MaxRoomsPerFloor];
            // Get player's grid-local position (for indexing into columnCenters/rowCenters arrays)
            var (playerGridRow, playerGridCol) = grid.GridPosition(playerRoom);
            int playerRowIdx = Math.Clamp(playerGridRow, 0, grid.Rows - 1);
            int playerColIdx = Math.Clamp(playerGridCol, 0, grid.Cols - 1);
            int applied = 0;
            for (int i = 0; i < roomCenters.Count; i++)
            {
                int col = columnAssignments[i];
                int row = rowAssignments[i];
                if (col < 0 || col >= grid.Cols || row < 0 || row >= grid.Rows)
                    continue;

                int idx = grid.RoomIndex(row, col);
                var predicted = ComposeWorld(mean, columnVector, rowVector, columnCenters[col], rowCenters[row]);
                predictedCenters[idx] = predicted;
                var actual = roomCenters[i];
                float delta = Vector2.Distance(actual, predicted);
                if (delta > ValidationTolerance)
                {
                    try
                    {
                        Service.Log.Info($"[RoomCenterGenerator] Room {idx} actual=({actual.X:F2},{actual.Y:F2}) predicted=({predicted.X:F2},{predicted.Y:F2}) dist={delta:F2}");
                    }
                    catch
                    {
                        // ignore logging issues
                    }
                    debug.PredictedCenters = (Vector2?[])predictedCenters.Clone();
                    debug.FailureRoomIndex = idx;
                    debug.FailureActualCenter = actual;
                    debug.FailurePredictedCenter = predicted;
                    debug.PlayerRoomCenter = ComposeWorld(mean, columnVector, rowVector, columnCenters[playerColIdx], rowCenters[playerRowIdx]);
                    debug.ActualCentersMask = (bool[])actualMask.Clone();
                    debug.FinalCenters = (Vector3?[])result.Clone();
                    debug.ColumnBasis = columnVector;
                    debug.RowBasis = rowVector;
                    debug.ColumnCoords = (float[])columnCenters.Clone();
                    debug.RowCoords = (float[])rowCenters.Clone();
                    debug.MeanPoint = mean;
                    debug.Error = $"Room {idx} deviates from predicted grid (> {ValidationTolerance}m).";
                    _latestDebugSnapshot = debug;
                    error = $"Room {idx} deviates from predicted grid (> {ValidationTolerance}m).";
                    return false;
                }

                float y = roomCenters3D[i].Y;
                if (result[idx].HasValue)
                {
                    var existing = result[idx]!.Value;
                    result[idx] = new Vector3(
                        (existing.X + actual.X) * 0.5f,
                        (existing.Y + y) * 0.5f,
                        (existing.Z + actual.Y) * 0.5f);
                }
                else
                {
                    result[idx] = new Vector3(actual.X, y, actual.Y);
                    applied++;
                    actualMask[idx] = true;
                }
            }

            centers = result;
            stats = new GenerationStats(activeLayout.Count, roomCenters.Count, applied, 0);
            try { Service.Log.Info($"[RoomCenterGenerator] Detected {grid.Cols}x{grid.Rows} grid ({grid.RoomCount} rooms), applied {applied} centers."); } catch { }
            debug.FinalCenters = (Vector3?[])result.Clone();
            debug.ActualCentersMask = actualMask;
            debug.PlayerRoomCenter = result[playerRoom].HasValue
                ? new Vector2(result[playerRoom]!.Value.X, result[playerRoom]!.Value.Z)
                : ComposeWorld(mean, columnVector, rowVector, columnCenters[playerColIdx], rowCenters[playerRowIdx]);
            debug.ColumnBasis = columnVector;
            debug.RowBasis = rowVector;
            debug.ColumnCoords = (float[])columnCenters.Clone();
            debug.RowCoords = (float[])rowCenters.Clone();
            debug.MeanPoint = mean;
            debug.PredictedCenters = (Vector2?[])predictedCenters.Clone();
            debug.Error = string.Empty;
            debug.AlignmentFailed = false;
            _latestDebugSnapshot = debug;
            return true;
        }

        // Minimum silhouette score improvement to prefer higher K
        private const float SilhouetteImprovementThreshold = 0.05f;

        private static (List<CollisionPoint> layout, int kUsed) SelectActiveLayout(List<CollisionPoint> points, Vector3 playerPos)
        {
            if (points.Count <= LayoutSplitThreshold)
                return (new List<CollisionPoint>(points), 1);

            // Try K=1,2,3 and pick the best based on silhouette score
            int bestK = 1;
            float bestScore = float.MinValue;
            int[]? bestAssignments = null;
            Vector2[]? bestCentroids = null;

            for (int k = 1; k <= 3; k++)
            {
                if (points.Count < k * 10)  // Need enough points per cluster
                    continue;

                var (assignments, centroids, score) = RunKMeansWithScore(points, k);
                if (assignments == null)
                    continue;

                // For K=1, silhouette is undefined, use 0 as baseline
                float effectiveScore = k == 1 ? 0f : score;

                // Prefer higher K only if it significantly improves the score
                if (effectiveScore > bestScore + SilhouetteImprovementThreshold || bestAssignments == null)
                {
                    bestK = k;
                    bestScore = effectiveScore;
                    bestAssignments = assignments;
                    bestCentroids = centroids;
                }
            }

            if (bestK == 1 || bestAssignments == null || bestCentroids == null)
            {
                try { Service.Log.Info($"[RoomCenterGenerator] Layout separation: {points.Count} points -> K=1 (single layout)."); } catch { }
                return (new List<CollisionPoint>(points), 1);
            }

            // Find cluster closest to player
            var playerXZ = new Vector2(playerPos.X, playerPos.Z);
            int chosen = FindNearestCluster(playerXZ, bestCentroids);

            var result = new List<CollisionPoint>();
            for (int i = 0; i < points.Count; i++)
            {
                if (bestAssignments[i] == chosen)
                    result.Add(points[i]);
            }

            try { Service.Log.Info($"[RoomCenterGenerator] Layout separation: {points.Count} points -> K={bestK} (silhouette={bestScore:F3}), selected {result.Count} points for active layout."); } catch { }
            return (result, bestK);
        }

        /// <summary>
        /// Runs k-means and returns assignments, centroids, and silhouette score.
        /// </summary>
        private static (int[]? assignments, Vector2[]? centroids, float silhouetteScore) RunKMeansWithScore(List<CollisionPoint> points, int k)
        {
            var assignments = RunKMeans(points, k);
            if (assignments == null)
                return (null, null, float.MinValue);

            var centroids = new Vector2[k];
            var counts = new int[k];
            for (int i = 0; i < points.Count; i++)
            {
                int idx = assignments[i];
                centroids[idx] += points[i].XZ;
                counts[idx]++;
            }

            for (int i = 0; i < k; i++)
            {
                if (counts[i] > 0)
                    centroids[i] /= counts[i];
            }

            // Calculate silhouette score (only meaningful for k >= 2)
            float silhouetteScore = 0f;
            if (k >= 2)
            {
                silhouetteScore = CalculateSilhouetteScore(points, assignments, centroids, k);
            }

            return (assignments, centroids, silhouetteScore);
        }

        /// <summary>
        /// Calculates the average silhouette score for the clustering.
        /// Score ranges from -1 (bad) to 1 (good). Higher is better.
        /// </summary>
        private static float CalculateSilhouetteScore(List<CollisionPoint> points, int[] assignments, Vector2[] centroids, int k)
        {
            if (points.Count == 0 || k < 2)
                return 0f;

            // Group points by cluster
            var clusterPoints = new List<Vector2>[k];
            for (int i = 0; i < k; i++)
                clusterPoints[i] = new List<Vector2>();
            for (int i = 0; i < points.Count; i++)
                clusterPoints[assignments[i]].Add(points[i].XZ);

            float totalSilhouette = 0f;
            int validPoints = 0;

            for (int i = 0; i < points.Count; i++)
            {
                var pointXZ = points[i].XZ;
                int cluster = assignments[i];
                var myCluster = clusterPoints[cluster];

                if (myCluster.Count <= 1)
                    continue;  // Can't compute silhouette for single-point cluster

                // a(i) = average distance to other points in same cluster
                float a = 0f;
                for (int j = 0; j < myCluster.Count; j++)
                {
                    if (myCluster[j] != pointXZ)
                        a += Vector2.Distance(pointXZ, myCluster[j]);
                }
                a /= (myCluster.Count - 1);

                // b(i) = minimum average distance to points in other clusters
                float b = float.MaxValue;
                for (int c = 0; c < k; c++)
                {
                    if (c == cluster || clusterPoints[c].Count == 0)
                        continue;

                    float avgDist = 0f;
                    for (int j = 0; j < clusterPoints[c].Count; j++)
                        avgDist += Vector2.Distance(pointXZ, clusterPoints[c][j]);
                    avgDist /= clusterPoints[c].Count;

                    if (avgDist < b)
                        b = avgDist;
                }

                if (b == float.MaxValue)
                    continue;

                // s(i) = (b - a) / max(a, b)
                float s = (b - a) / MathF.Max(a, b);
                totalSilhouette += s;
                validPoints++;
            }

            return validPoints > 0 ? totalSilhouette / validPoints : 0f;
        }

        private static int FindNearestCluster(Vector2 point, Vector2[] centroids)
        {
            int chosen = 0;
            float bestDist = float.MaxValue;
            for (int i = 0; i < centroids.Length; i++)
            {
                float dist = Vector2.DistanceSquared(point, centroids[i]);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    chosen = i;
                }
            }
            return chosen;
        }

        private static int[]? RunKMeans(List<CollisionPoint> points, int k)
        {
            if (points.Count < k)
                return null;

            Span<Vector2> centroids = stackalloc Vector2[k];
            centroids[0] = points[0].XZ;
            for (int i = 1; i < k; i++)
            {
                centroids[i] = points[(i * points.Count) / k].XZ;
            }

            var assignments = new int[points.Count];
            Span<Vector2> sums = stackalloc Vector2[k];
            Span<int> counts = stackalloc int[k];

            for (int iter = 0; iter < 8; iter++)
            {
                Array.Clear(assignments, 0, assignments.Length);
                sums.Clear();
                counts.Clear();
                for (int pi = 0; pi < points.Count; pi++)
                {
                    int idx = FindNearestCentroid(points[pi].XZ, centroids);
                    assignments[pi] = idx;
                    sums[idx] += points[pi].XZ;
                    counts[idx]++;
                }

                for (int ci = 0; ci < k; ci++)
                {
                    if (counts[ci] > 0)
                        centroids[ci] = sums[ci] / counts[ci];
                }
            }

            return assignments;
        }

        private static int FindNearestCentroid(Vector2 point, Span<Vector2> centroids)
        {
            int idx = 0;
            float best = float.MaxValue;
            for (int i = 0; i < centroids.Length; i++)
            {
                float d = Vector2.DistanceSquared(point, centroids[i]);
                if (d < best)
                {
                    best = d;
                    idx = i;
                }
            }
            return idx;
        }

        private static List<Vector3> CollapseRespawnWalls(List<CollisionPoint> points)
        {
            var clusters = ClusterRespawnWalls(points);
            var centers = new List<Vector3>(clusters.Count);
            for (int i = 0; i < clusters.Count; i++)
                centers.Add(clusters[i].Center);
            return centers;
        }

        private static bool RemoveIsolatedRespawnWalls(List<CollisionPoint> points)
        {
            if (points == null || points.Count == 0)
                return false;

            var clusters = ClusterRespawnWalls(points);
            if (clusters.Count == 0)
                return false;

            float neighborDistanceSq = IsolatedRoomNeighborDistance * IsolatedRoomNeighborDistance;
            var isolated = new bool[clusters.Count];
            bool anyIsolated = false;

            for (int i = 0; i < clusters.Count; i++)
            {
                bool hasNeighbor = false;
                var currentXZ = new Vector2(clusters[i].Center.X, clusters[i].Center.Z);
                for (int j = 0; j < clusters.Count; j++)
                {
                    if (i == j)
                        continue;

                    if (Vector2.DistanceSquared(currentXZ, new Vector2(clusters[j].Center.X, clusters[j].Center.Z)) <= neighborDistanceSq)
                    {
                        hasNeighbor = true;
                        break;
                    }
                }

                if (!hasNeighbor)
                {
                    isolated[i] = true;
                    anyIsolated = true;
                }
            }

            if (!anyIsolated)
                return false;

            var removalMask = new bool[points.Count];
            for (int i = 0; i < clusters.Count; i++)
            {
                if (!isolated[i])
                    continue;

                var members = clusters[i].Members;
                for (int j = 0; j < members.Count; j++)
                {
                    int idx = members[j];
                    if (idx >= 0 && idx < removalMask.Length)
                        removalMask[idx] = true;
                }
            }

            for (int i = points.Count - 1; i >= 0; i--)
            {
                if (removalMask[i])
                    points.RemoveAt(i);
            }

            return true;
        }

        private static List<RespawnCluster> ClusterRespawnWalls(List<CollisionPoint> points)
        {
            var clusters = new List<RespawnCluster>();
            if (points == null || points.Count == 0)
                return clusters;

            var visited = new bool[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                if (visited[i])
                    continue;

                var queue = new Queue<int>();
                queue.Enqueue(i);
                visited[i] = true;

                int count = 0;
                float sumX = 0f, sumZ = 0f;
                float boundsMinY = float.MaxValue, boundsMaxY = float.MinValue;
                var members = new List<int>();

                while (queue.Count > 0)
                {
                    int idx = queue.Dequeue();
                    count++;
                    sumX += points[idx].Center.X;
                    sumZ += points[idx].Center.Z;
                    if (points[idx].BoundsMinY < boundsMinY) boundsMinY = points[idx].BoundsMinY;
                    if (points[idx].BoundsMaxY > boundsMaxY) boundsMaxY = points[idx].BoundsMaxY;
                    members.Add(idx);

                    for (int j = 0; j < points.Count; j++)
                    {
                        if (visited[j])
                            continue;
                        if (Vector2.Distance(points[idx].XZ, points[j].XZ) <= WithinRoomLinkDistance)
                        {
                            visited[j] = true;
                            queue.Enqueue(j);
                        }
                    }
                }

                if (count >= MinRespawnWallsPerRoom)
                {
                    float y = boundsMinY + (boundsMaxY - boundsMinY) * 0.25f;
                    clusters.Add(new RespawnCluster(new Vector3(sumX / count, y, sumZ / count), members));
                }
            }

            return clusters;
        }

        private static Vector2 ComputeMean(List<Vector2> points)
        {
            Vector2 sum = Vector2.Zero;
            for (int i = 0; i < points.Count; i++)
                sum += points[i];
            return sum / points.Count;
        }

        private static AxisClusterResult? RunAxisKMeans(float[] values, int k)
        {
            if (values.Length < k)
                return null;

            var centers = new float[k];
            float min = float.MaxValue;
            float max = float.MinValue;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] < min) min = values[i];
                if (values[i] > max) max = values[i];
            }

            if (MathF.Abs(max - min) < 1e-3f)
            {
                for (int i = 0; i < k; i++)
                    centers[i] = min;
            }
            else
            {
                for (int i = 0; i < k; i++)
                    centers[i] = min + (max - min) * i / Math.Max(1, k - 1);
            }

            var assignments = new int[values.Length];
            var sums = new float[k];
            var counts = new int[k];

            for (int iter = 0; iter < 12; iter++)
            {
                Array.Clear(sums);
                Array.Clear(counts);
                for (int vi = 0; vi < values.Length; vi++)
                {
                    int idx = FindNearestIndex(values[vi], centers);
                    assignments[vi] = idx;
                    sums[idx] += values[vi];
                    counts[idx]++;
                }

                for (int ci = 0; ci < k; ci++)
                {
                    if (counts[ci] > 0)
                        centers[ci] = sums[ci] / counts[ci];
                }
            }

            var order = new int[k];
            for (int i = 0; i < k; i++)
                order[i] = i;
            Array.Sort(order, (a, b) => centers[a].CompareTo(centers[b]));

            var ranks = new int[k];
            for (int i = 0; i < k; i++)
                ranks[order[i]] = i;

            var sortedCenters = new float[k];
            for (int i = 0; i < k; i++)
                sortedCenters[i] = centers[order[i]];

            for (int i = 0; i < assignments.Length; i++)
                assignments[i] = ranks[assignments[i]];

            return new AxisClusterResult(k, assignments, sortedCenters);
        }

        private static int FindNearestIndex(float value, float[] centers)
        {
            int idx = 0;
            float best = float.MaxValue;
            for (int i = 0; i < centers.Length; i++)
            {
                float d = MathF.Abs(value - centers[i]);
                if (d < best)
                {
                    best = d;
                    idx = i;
                }
            }
            return idx;
        }

        private static bool TryAlignAxes(GridConfig grid, int playerRoom, int playerCenterIdx,
            AxisClusterResult primary, AxisClusterResult secondary,
            Vector2 axisPrimary, Vector2 axisSecondary, bool primaryIsColumn,
            out int[] columnAssignments, out int[] rowAssignments,
            out float[] columnCenters, out float[] rowCenters,
            out Vector2 columnVector, out Vector2 rowVector)
        {
            columnAssignments = Array.Empty<int>();
            rowAssignments = Array.Empty<int>();
            columnCenters = Array.Empty<float>();
            rowCenters = Array.Empty<float>();
            columnVector = Vector2.Zero;
            rowVector = Vector2.Zero;

            // Get player's grid-local position (0-based within the grid)
            var (playerGridRow, playerGridCol) = grid.GridPosition(playerRoom);
            if (playerGridRow < 0 || playerGridCol < 0)
                return false;  // Player room not in this grid
            
            int playerCol = playerGridCol;
            int playerRow = playerGridRow;
            int primaryAssign = primary.Assignments[playerCenterIdx];
            int secondaryAssign = secondary.Assignments[playerCenterIdx];

            bool rowReversedSecondary;
            bool colReversedPrimary;
            if (!(AlignAxis(primary, grid.Cols, playerCol, primaryAssign, out colReversedPrimary) &&
                  AlignAxis(secondary, grid.Rows, playerRow, secondaryAssign, out rowReversedSecondary)))
                return false;
            columnAssignments = primary.Assignments;
            rowAssignments = secondary.Assignments;
            columnCenters = primary.Centers;
            rowCenters = secondary.Centers;
            columnVector = colReversedPrimary ? -axisPrimary : axisPrimary;
            rowVector = rowReversedSecondary ? -axisSecondary : axisSecondary;

            return true;
        }

        private static bool AlignAxis(AxisClusterResult axis, int expectedCount, int targetIndex, int playerAssignment, out bool reversed)
        {
            reversed = false;
            if (axis.ClusterCount != expectedCount)
                return false;

            if (playerAssignment == targetIndex)
                return true;

            if ((axis.ClusterCount - 1 - playerAssignment) == targetIndex)
            {
                axis.Reverse();
                reversed = true;
                return true;
            }

            return false;
        }

        private static Vector2 ComposeWorld(Vector2 mean, Vector2 columnAxis, Vector2 rowAxis, float columnCoord, float rowCoord)
        {
            return mean + columnAxis * columnCoord + rowAxis * rowCoord;
        }

        private static int FindNearestIndex(List<Vector2> points, Vector2 reference)
        {
            int idx = 0;
            float best = float.MaxValue;
            for (int i = 0; i < points.Count; i++)
            {
                float d = Vector2.DistanceSquared(points[i], reference);
                if (d < best)
                {
                    best = d;
                    idx = i;
                }
            }
            return idx;
        }
    }
}

