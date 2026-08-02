using System;
using System.Numerics;

namespace DeepDungeon.Fsd.Core
{
    public static class Pt30BossGeometry
    {
        public const float BossEngageArrivalRadius = 0.8f;

        private const float ArenaWallMargin = 0.4f;
        private const float ProbeRadius = 0.45f;
        private const float BossEngageRadius = 14.8f;
        private const float OrbitRadiusTarget = 16.5f;
        private const float OrbitRadiusMin = 15.2f;
        private const float OrbitRadiusMax = 17.8f;
        private const float OrbitLookaheadDistance = 10.0f;
        private const float OrbitSegmentProbeDistance = 5.0f;
        private const float OrbitRadialCorrectionMax = 0.45f;

        private static readonly Vector2 ArenaCenter = new(-300f, -300f);
        private static readonly Vector2 EastLobeCenter = new(-283.51849f, -300f);
        private static readonly Vector2 WestLobeCenter = new(-316.48102f, -300f);
        private static readonly Vector2 SouthSpokeCenter = new(-300f, -281.5f);
        private static readonly Vector2 NorthSpokeCenter = new(-300f, -318.5f);

        public static bool TryFindBossEngageDestination(Vector2 playerPosition, out Vector2 destination)
        {
            var radial = playerPosition - ArenaCenter;
            if (radial.LengthSquared() < 0.01f)
                radial = SouthSpokeCenter - ArenaCenter;

            var direction = Vector2.Normalize(radial);
            Vector2[] candidates =
            [
                ArenaCenter + (direction * BossEngageRadius),
                new Vector2(-300f, -285.2f),
                new Vector2(-300f, -314.8f),
                new Vector2(-285.2f, -300f),
                new Vector2(-314.8f, -300f)
            ];

            foreach (var candidate in candidates)
            {
                if (!IsInsideArena(candidate))
                    continue;

                destination = candidate;
                return true;
            }

            destination = default;
            return false;
        }

        public static bool TryFindOrbitDestination(Vector2 playerPosition, int orbitDirection, out Vector2 destination)
        {
            int effectiveDirection = orbitDirection == 0
                ? ChooseOrbitDirection(playerPosition)
                : Math.Sign(orbitDirection);

            if (TryFindTangentDestination(playerPosition, effectiveDirection, out destination))
                return true;

            var radial = playerPosition - ArenaCenter;
            if (radial.LengthSquared() < 0.01f)
                radial = Vector2.UnitY;

            float angle = MathF.Atan2(radial.Y, radial.X);
            float currentRadius = radial.Length();
            float[] radiusCandidates =
            [
                OrbitRadiusTarget,
                Math.Clamp(currentRadius, OrbitRadiusMin, OrbitRadiusMax),
                16.0f,
                17.2f
            ];
            float[] stepCandidatesDeg = [30f, 24f, 18f, 12f];

            foreach (float stepDeg in stepCandidatesDeg)
            {
                float candidateAngle = angle + (effectiveDirection * DegreesToRadians(stepDeg));
                var dir = new Vector2(MathF.Cos(candidateAngle), MathF.Sin(candidateAngle));

                for (int i = 0; i < radiusCandidates.Length; i++)
                {
                    var candidate = ArenaCenter + (dir * radiusCandidates[i]);
                    if (!IsInsideArena(candidate))
                        continue;

                    destination = candidate;
                    return true;
                }
            }

            destination = default;
            return false;
        }

        public static int ChooseOrbitDirection(Vector2 playerPosition)
        {
            int bestDirection = 1;
            int bestScore = int.MinValue;

            foreach (int direction in new[] { 1, -1 })
            {
                if (!TryFindTangentDestination(playerPosition, direction, out var candidate))
                    continue;

                int score = ComputeMarginScore(candidate);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestDirection = direction;
                }
            }

            return bestDirection;
        }

        public static bool IsInsideArena(Vector2 point)
        {
            if (IsInsideDonut(point, ArenaCenter, 14f + ArenaWallMargin, 19f - ArenaWallMargin))
                return true;

            if (IsInsideCircle(point, EastLobeCenter, 7.5f - ArenaWallMargin))
                return true;

            if (IsInsideCircle(point, WestLobeCenter, 7.5f - ArenaWallMargin))
                return true;

            if (IsInsideRect(point, SouthSpokeCenter, 5f - ArenaWallMargin, 9.5f - ArenaWallMargin))
                return true;

            if (IsInsideRect(point, NorthSpokeCenter, 5f - ArenaWallMargin, 9.5f - ArenaWallMargin))
                return true;

            return false;
        }

        private static int ComputeMarginScore(Vector2 candidate)
        {
            int score = 0;
            ReadOnlySpan<Vector2> probes =
            [
                Vector2.Zero,
                new Vector2(ProbeRadius, 0f),
                new Vector2(-ProbeRadius, 0f),
                new Vector2(0f, ProbeRadius),
                new Vector2(0f, -ProbeRadius),
                new Vector2(ProbeRadius * 0.7f, ProbeRadius * 0.7f),
                new Vector2(-ProbeRadius * 0.7f, ProbeRadius * 0.7f),
                new Vector2(ProbeRadius * 0.7f, -ProbeRadius * 0.7f),
                new Vector2(-ProbeRadius * 0.7f, -ProbeRadius * 0.7f)
            ];

            foreach (var probe in probes)
            {
                if (IsInsideArena(candidate + probe))
                    score++;
            }

            return score;
        }

        private static bool TryFindTangentDestination(Vector2 playerPosition, int orbitDirection, out Vector2 destination)
        {
            var radial = playerPosition - ArenaCenter;
            if (radial.LengthSquared() < 0.01f)
                radial = Vector2.UnitY;

            var radialDir = Vector2.Normalize(radial);
            var tangent = new Vector2(-radialDir.Y, radialDir.X) * Math.Sign(orbitDirection == 0 ? 1 : orbitDirection);
            float radius = radial.Length();
            float radialCorrection = Math.Clamp((OrbitRadiusTarget - radius) / 4.0f, -OrbitRadialCorrectionMax, OrbitRadialCorrectionMax);
            var desiredDirection = Vector2.Normalize(tangent + radialDir * radialCorrection);

            float[] distances =
            [
                OrbitLookaheadDistance,
                8.0f,
                6.0f
            ];

            foreach (float distance in distances)
            {
                var candidate = playerPosition + desiredDirection * distance;
                if (!IsInsideArena(candidate))
                    continue;
                if (!IsSegmentInsideArena(playerPosition, candidate, Math.Min(distance, OrbitSegmentProbeDistance)))
                    continue;

                destination = candidate;
                return true;
            }

            destination = default;
            return false;
        }

        private static bool IsSegmentInsideArena(Vector2 from, Vector2 to, float probeDistance)
        {
            var delta = to - from;
            float length = delta.Length();
            if (length < 0.01f)
                return IsInsideArena(from);

            var dir = delta / length;
            float clampedProbeDistance = Math.Min(length, probeDistance);
            for (float distance = 0.75f; distance <= clampedProbeDistance; distance += 0.75f)
            {
                if (!IsInsideArena(from + dir * distance))
                    return false;
            }

            return true;
        }

        private static bool IsInsideCircle(Vector2 point, Vector2 center, float radius)
        {
            return Vector2.DistanceSquared(point, center) <= radius * radius;
        }

        private static bool IsInsideDonut(Vector2 point, Vector2 center, float innerRadius, float outerRadius)
        {
            float distanceSquared = Vector2.DistanceSquared(point, center);
            return distanceSquared >= innerRadius * innerRadius && distanceSquared <= outerRadius * outerRadius;
        }

        private static bool IsInsideRect(Vector2 point, Vector2 center, float halfWidth, float halfHeight)
        {
            float dx = MathF.Abs(point.X - center.X);
            float dy = MathF.Abs(point.Y - center.Y);
            return dx <= halfWidth && dy <= halfHeight;
        }

        private static float DegreesToRadians(float degrees)
        {
            return degrees * (MathF.PI / 180f);
        }
    }
}
