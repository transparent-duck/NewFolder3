using System;
using DeepDungeon.Fsd.Core;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.GameState
{
    /// <summary>
    /// Tracks floor-set hoard count and the three guaranteed distribution segments.
    /// FSD enters at x1, so the segment mask is built directly from observed
    /// hoard-count increases during the supported run.
    /// </summary>
    internal static class DeepDungeonFloorsetTracker
    {
        private static uint _lastDungeonId;
        private static int _currentFloorsetStart;
        private static int _floorsetBandedFoundCount;
        private static int _satisfiedSegmentMask;
        private static int _lastHoardCount = -1;
        private static bool _hasCurrentSample;
        private static DateTime _lastReadFailureLogAt = DateTime.MinValue;

        public static int CurrentFloorsetStart => _currentFloorsetStart;

        public static bool TryGetCurrentFloorsetState(
            byte floor,
            out FloorsetHoardDistributionState state)
        {
            int floorsetStart = GetFloorsetStart(floor);
            if (!_hasCurrentSample ||
                floorsetStart <= 0 ||
                floorsetStart != _currentFloorsetStart)
            {
                state = new FloorsetHoardDistributionState(0, 0);
                return false;
            }

            state = new FloorsetHoardDistributionState(
                _floorsetBandedFoundCount,
                _satisfiedSegmentMask);
            return true;
        }

        public static FloorsetHoardOpportunity GetCurrentOpportunity(byte floor)
        {
            TryGetCurrentFloorsetState(floor, out FloorsetHoardDistributionState state);
            return FloorsetHoardDistributionPolicy.Decide(state, floor);
        }

        public static void Reset()
        {
            _lastDungeonId = 0;
            _currentFloorsetStart = 0;
            _floorsetBandedFoundCount = 0;
            _satisfiedSegmentMask = 0;
            _lastHoardCount = -1;
            _hasCurrentSample = false;
            _lastReadFailureLogAt = DateTime.MinValue;
        }

        public static unsafe bool Update(
            InstanceContentDeepDungeon* dd,
            bool isTransitioning)
        {
            if (dd == null)
            {
                _hasCurrentSample = false;
                return false;
            }

            try
            {
                uint dungeonId = dd->DeepDungeonId;
                byte floor = dd->Floor;
                int floorsetStart = GetFloorsetStart(floor);
                if (floorsetStart <= 0)
                {
                    _hasCurrentSample = false;
                    return false;
                }

                if (isTransitioning)
                    return false;

                int hoardCount = dd->HoardCount;
                return UpdateSample(dungeonId, floor, hoardCount);
            }
            catch (Exception ex)
            {
                _hasCurrentSample = false;
                if ((DateTime.UtcNow - _lastReadFailureLogAt).TotalSeconds >= 2)
                {
                    _lastReadFailureLogAt = DateTime.UtcNow;
                    Service.Log.Error($"[DeepDungeonFloorsetTracker] State read failed: {ex}");
                }
                return false;
            }
        }

        internal static bool UpdateSample(
            uint dungeonId,
            byte floor,
            int hoardCount)
        {
            int floorsetStart = GetFloorsetStart(floor);
            if (floorsetStart <= 0)
            {
                _hasCurrentSample = false;
                return false;
            }

            if (dungeonId != _lastDungeonId ||
                floorsetStart != _currentFloorsetStart)
            {
                _lastDungeonId = dungeonId;
                _currentFloorsetStart = floorsetStart;
                _satisfiedSegmentMask = 0;
                _lastHoardCount = hoardCount;
                _floorsetBandedFoundCount = hoardCount;
                _hasCurrentSample = true;
                return true;
            }

            if (hoardCount > _lastHoardCount)
            {
                _satisfiedSegmentMask |=
                    FloorsetHoardDistributionPolicy.GetSegmentBit(floor);
            }

            _lastHoardCount = hoardCount;
            _floorsetBandedFoundCount = hoardCount;
            _hasCurrentSample = true;
            return true;
        }

        private static int GetFloorsetStart(byte floor)
        {
            return floor == 0 ? 0 : ((floor - 1) / 10) * 10 + 1;
        }
    }
}
