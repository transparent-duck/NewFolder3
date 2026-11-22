using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using DeepDungeon.Fsd.Dalamud.Utilities;
using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud.Movement
{
    /// <summary>
    /// Approaches and orbits a target using normal direct movement input until the target is defeated or stopped.
    /// </summary>
    public sealed class SpinningMob : IDisposable
    {
        // Passive observers do not acquire input or control the strategy.
        public static event Action<long, long>? OrbitTimingRecorded;
        internal static void RecordOrbitTiming(long start, long end) => OrbitTimingRecorded?.Invoke(start, end);
        public const float DefaultOrbitDistance = 1.25f;
        private const float MinOrbitDistance = 0.4f;
        private const float FastOrbitDistance = 0.65f;
        private const float SlowOrbitDistance = 2.05f;
        private const float ApproachBlendBand = 1.0f;
        private const float RadialCorrectionGain = 1.6f;
        private const float OutwardCorrectionGain = 2.2f;
        private const float MaxOutwardWeight = 1.0f;
        private const float CenterEscapeThreshold = 0.05f;
        private const float CloseApproachReselectThreshold = 2.5f;
        private const float TargetMovingSpeedThreshold = 0.8f;
        private const double TargetVelocityWindowSeconds = 0.15;
        private const double TargetRotationWindowSeconds = 0.25;
        private const float TargetRotationLockEpsilon = 0.04f;
        private const float RearHoldCenterDeg = 175f;
        private const float RearHoldDeadbandDeg = 12f;
        private const float RearHoldOrbitRadius = 1.45f;

        private const float PhaseRampUpStartDeg = 185f;
        private const float PhaseRampUpEndDeg = 250f;
        private const float PhaseHoldEndDeg = 300f;
        private const float PhaseRampDownEndDeg = 330f;

        private readonly DirectMovementController _movement = new("spinning mob");
        private bool _isActive;
        private object? _updateOwner;
        private bool _disposed;
        private ulong _targetId;
        private IGameObject? _target;
        private float _orbitDistance = DefaultOrbitDistance;
        private bool _adaptivePhase = true;
        private float _spinSign = 1f;
        private bool _hasEnteredCloseApproach;
        private bool _hasPrevTargetPos;
        private bool _isTargetMoving;
        private Vector2 _prevTargetPos;
        private long _lastUpdateTimestamp;
        private bool _hasPrevTargetRot;
        private float _prevTargetRot;
        private long _lastRotSampleTimestamp;
        private bool _isTargetRotationLocked;
        private Vector2 _lastDirection = Vector2.UnitX;
        private long _lastSpinTick, _nextGapLog;
        private bool _wasCasting;

        public bool IsActive => _isActive;
        public IGameObject? CurrentTarget => _target;
        public float OrbitDistance => _orbitDistance;
        public bool WasCancelledByUser { get; private set; }

        public bool Toggle(IGameObject? target, float distance = DefaultOrbitDistance, bool adaptivePhase = true)
        {
            if (_isActive)
            {
                Stop();
                return false;
            }

            return Start(target, distance, adaptivePhase);
        }

        public bool Start(IGameObject? target, float distance = DefaultOrbitDistance, bool adaptivePhase = true)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (target == null || target.IsDead)
            {
                Service.Log.Warning("[SpinningMob] Cannot start: no living target specified.");
                return false;
            }

            float sanitizedDistance = float.IsFinite(distance)
                ? MathF.Max(MinOrbitDistance, distance)
                : DefaultOrbitDistance;

            _target = target;
            _updateOwner = null;
            _targetId = target.GameObjectId;
            _orbitDistance = sanitizedDistance;
            _adaptivePhase = adaptivePhase;
            _isActive = true;
            _hasPrevTargetPos = false;
            _isTargetMoving = false;
            _lastUpdateTimestamp = 0;
            _hasPrevTargetRot = false;
            _lastRotSampleTimestamp = 0;
            _isTargetRotationLocked = false;
            _lastSpinTick = _nextGapLog = 0;
            _wasCasting = false;
            WasCancelledByUser = false;

            var player = Service.LocalPlayer;
            if (player != null)
            {
                Vector2 radial = new Vector2(player.Position.X - target.Position.X, player.Position.Z - target.Position.Z);
                _spinSign = ChooseSpinSign(radial, target.Rotation);
                _hasEnteredCloseApproach = radial.Length() <= CloseApproachReselectThreshold;
            }
            else
            {
                _spinSign = 1f;
                _hasEnteredCloseApproach = false;
            }

            _movement.ClearCancellation();
            _movement.ObserveUserInput = true;
            _movement.CancelOnManualInput = true;

            RecordSpinDiagnostic("started", 0);

            Service.Log.Info(adaptivePhase
                ? $"[SpinningMob] Started approach & phase-locked orbit for '{target.Name.TextValue}' (id={_targetId}) in [{FastOrbitDistance:F2}m, {SlowOrbitDistance:F2}m], spinSign={_spinSign:+#;-#;0}"
                : $"[SpinningMob] Started approach & orbit for '{target.Name.TextValue}' (id={_targetId}) at fixed radius {sanitizedDistance:F2}m, spinSign={_spinSign:+#;-#;0}");
            return true;
        }

        public void Stop(bool userCancelled = false)
        {
            if (_isActive)
            {
                Service.Log.Info(userCancelled
                    ? "[SpinningMob] Stopped: user movement input detected."
                    : "[SpinningMob] Stopped orbiting.");
            }

            WasCancelledByUser = userCancelled;
            _isActive = false;
            _updateOwner = null;
            _target = null;
            _targetId = 0;
            ReleaseMovement();
        }

        // Commands use the ordinary movement tick. FSD ticks only its own session.
        public void Update()
        {
            if (_updateOwner == null) TryUpdateMovement();
        }

        internal bool IsOwnedBy(object owner) => _isActive && ReferenceEquals(_updateOwner, owner);

        internal bool StartOwned(IGameObject target, object owner)
        {
            if (_isActive || !Start(target)) return false;
            _updateOwner = owner;
            return true;
        }

        internal bool TryUpdateOwned(object owner) => IsOwnedBy(owner) && TryUpdateMovement();

        internal void StopOwned(object owner)
        {
            if (IsOwnedBy(owner)) Stop();
        }

        private bool TryUpdateMovement()
        {
            if (_disposed || !_isActive)
                return false;

            if (_movement.WasCancelledByUser || UserMovementInput.IsActive)
            {
                Stop(userCancelled: true);
                return false;
            }

            var player = Service.LocalPlayer;
            if (player == null || player.IsDead)
            {
                Stop();
                return false;
            }

            long spinTick = System.Diagnostics.Stopwatch.GetTimestamp();
            double updateGap = _lastSpinTick == 0 ? 0 :
                System.Diagnostics.Stopwatch.GetElapsedTime(_lastSpinTick, spinTick).TotalMilliseconds;
            _lastSpinTick = spinTick;
            if (updateGap >= 150 && spinTick >= _nextGapLog)
            {
                _nextGapLog = spinTick + System.Diagnostics.Stopwatch.Frequency;
                RecordSpinDiagnostic("long-update-gap", updateGap);
            }

            if (Service.Condition[ConditionFlag.BetweenAreas] ||
                Service.Condition[ConditionFlag.BetweenAreas51])
            {
                Stop();
                return false;
            }

            var liveTarget = ResolveLiveTarget();
            if (liveTarget == null || liveTarget.IsDead)
            {
                Stop();
                return false;
            }

            _target = liveTarget;

            if (MechanicExecutionState.MovementBlocked)
            {
                _movement.Release();
                return false;
            }

            if (DirectMovementController.ActiveOwner is { } activeOwner &&
                activeOwner != _movement.Name)
            {
                return false;
            }

            var playerPos = new Vector2(player.Position.X, player.Position.Z);
            var targetPos = new Vector2(liveTarget.Position.X, liveTarget.Position.Z);
            Vector2 radial = playerPos - targetPos;
            float dist = radial.Length();

            long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!_hasPrevTargetPos)
            {
                _prevTargetPos = targetPos;
                _hasPrevTargetPos = true;
                _lastUpdateTimestamp = nowTicks;
            }
            else
            {
                double dt = (double)(nowTicks - _lastUpdateTimestamp) / System.Diagnostics.Stopwatch.Frequency;
                if (dt >= TargetVelocityWindowSeconds)
                {
                    float targetSpeed = Vector2.Distance(targetPos, _prevTargetPos) / (float)dt;
                    _isTargetMoving = targetSpeed > TargetMovingSpeedThreshold;
                    _prevTargetPos = targetPos;
                    _lastUpdateTimestamp = nowTicks;
                }
            }

            if (!_hasEnteredCloseApproach && dist <= CloseApproachReselectThreshold)
            {
                _spinSign = ChooseSpinSign(radial, liveTarget.Rotation);
                _hasEnteredCloseApproach = true;
            }

            if (!_hasPrevTargetRot)
            {
                _prevTargetRot = liveTarget.Rotation;
                _hasPrevTargetRot = true;
                _lastRotSampleTimestamp = nowTicks;
            }
            else
            {
                float rotDelta = MathF.Abs(WrapSignedAngle(liveTarget.Rotation - _prevTargetRot));
                if (_isTargetRotationLocked)
                {
                    if (rotDelta >= TargetRotationLockEpsilon)
                    {
                        _isTargetRotationLocked = false;
                        _prevTargetRot = liveTarget.Rotation;
                        _lastRotSampleTimestamp = nowTicks;
                        _spinSign = ChooseSpinSign(radial, liveTarget.Rotation);
                    }
                }
                else
                {
                    double rotDt = (double)(nowTicks - _lastRotSampleTimestamp) / System.Diagnostics.Stopwatch.Frequency;
                    if (rotDt >= TargetRotationWindowSeconds)
                    {
                        if (rotDelta < TargetRotationLockEpsilon)
                            _isTargetRotationLocked = true;
                        _prevTargetRot = liveTarget.Rotation;
                        _lastRotSampleTimestamp = nowTicks;
                    }
                }
            }

            bool isCasting = liveTarget is IBattleChara battleChara && battleChara.IsCasting;
            if (isCasting && !_wasCasting) RecordSpinDiagnostic("boss-started-cast", updateGap);
            _wasCasting = isCasting;
            bool isLegacyMoveMode = OmenTools.DService.Instance().GameConfig.UiControl.TryGetUInt("MoveMode", out var mode) && mode == 1;

            if (_adaptivePhase && !_isTargetMoving && dist <= CloseApproachReselectThreshold && (isCasting || _isTargetRotationLocked))
            {
                if (TryComputeRearHoldDirection(radial, dist, player.Rotation, liveTarget.Rotation, isLegacyMoveMode, out var rearDirection))
                {
                    _lastDirection = rearDirection;
                    _movement.ObserveUserInput = true;
                    _movement.CancelOnManualInput = true;
                    _movement.SetWorldDirectionalInput(rearDirection);
                    return _movement.ControlsAvailable;
                }
                // Holding behind the boss is an intentional orbit decision.
                _movement.Release();
                return true;
            }

            float targetRadius = _isTargetMoving && _adaptivePhase
                ? FastOrbitDistance
                : ComputeTargetRadius(radial, dist, liveTarget.Rotation);

            var direction = ComputeDesiredWorldDirection(
                radial,
                dist,
                player.Rotation,
                targetRadius,
                _spinSign,
                isLegacyMoveMode);

            _lastDirection = direction;
            _movement.ObserveUserInput = true;
            _movement.CancelOnManualInput = true;
            _movement.SetWorldDirectionalInput(direction);
            return _movement.ControlsAvailable;
        }

        private IGameObject? ResolveLiveTarget()
        {
            if (_targetId == 0)
                return null;

            return Service.GameObjects.SearchById(_targetId);
        }

        private unsafe void RecordSpinDiagnostic(string reason, double updateGapMs)
        {
            if (Service.LocalPlayer is not { } player || _target is not { } target) return;
            var radial = new Vector2(player.Position.X - target.Position.X, player.Position.Z - target.Position.Z);
            float angle = MathF.Atan2(radial.X, radial.Y);
            var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            var settings = OmenTools.DService.Instance().GameConfig.System;
            uint? Setting(string name) => settings.TryGetUInt(name, out var value) ? value : null;
            Service.Log.Info("[SpinningMob.Diagnostic] " + System.Text.Json.JsonSerializer.Serialize(new
            {
                reason, updateGapMs, targetId = _targetId,
                distance = radial.Length(), phaseDegrees = WrapSignedAngle(angle - target.Rotation) * (180f / MathF.PI),
                spinSign = _spinSign, targetMoving = _isTargetMoving, rotationLocked = _isTargetRotationLocked,
                targetRotation = target.Rotation, playerRotation = player.Rotation,
                playerCasting = player.IsCasting, bossCasting = (target as IBattleChara)?.IsCasting,
                bossAction = (target as IBattleChara)?.CastActionId,
                owner = DirectMovementController.ActiveOwner,
                frameSeconds = framework == null ? (float?)null : framework->FrameDeltaTime,
                windowInactive = framework == null ? (bool?)null : framework->WindowInactive,
                fpsLimit = Setting("Fps"), backgroundLimit = Setting("FPSInActive"), idleLimit = Setting("FPSDownAFK")
            }));
        }

        private static float ChooseSpinSign(Vector2 radial, float targetRotation)
        {
            if (radial.LengthSquared() <= CenterEscapeThreshold * CenterEscapeThreshold)
                return 1f;

            float playerAngle = MathF.Atan2(radial.X, radial.Y);
            float rel = WrapSignedAngle(playerAngle - targetRotation);
            return rel >= 0f ? 1f : -1f;
        }

        private float ComputeTargetRadius(Vector2 radial, float dist, float targetRotation)
        {
            if (!_adaptivePhase)
                return _orbitDistance;

            if (dist <= CenterEscapeThreshold || dist > CloseApproachReselectThreshold)
                return FastOrbitDistance;

            float playerAngle = MathF.Atan2(radial.X, radial.Y);
            float aheadRad = WrapPositiveAngle(_spinSign * (playerAngle - targetRotation));
            float aheadDeg = aheadRad * (180f / MathF.PI);

            if (aheadDeg <= PhaseRampUpStartDeg)
                return FastOrbitDistance;

            if (aheadDeg <= PhaseRampUpEndDeg)
            {
                float t = (aheadDeg - PhaseRampUpStartDeg) / (PhaseRampUpEndDeg - PhaseRampUpStartDeg);
                return FastOrbitDistance + t * (SlowOrbitDistance - FastOrbitDistance);
            }

            if (aheadDeg <= PhaseHoldEndDeg)
                return SlowOrbitDistance;

            if (aheadDeg <= PhaseRampDownEndDeg)
            {
                float t = (aheadDeg - PhaseHoldEndDeg) / (PhaseRampDownEndDeg - PhaseHoldEndDeg);
                return SlowOrbitDistance - t * (SlowOrbitDistance - FastOrbitDistance);
            }

            return FastOrbitDistance;
        }

        private static float WrapSignedAngle(float angle)
        {
            return MathF.IEEERemainder(angle, MathF.Tau);
        }

        private static float WrapPositiveAngle(float angle)
        {
            float wrapped = angle % MathF.Tau;
            return wrapped < 0f ? wrapped + MathF.Tau : wrapped;
        }

        private bool TryComputeRearHoldDirection(
            Vector2 radial,
            float dist,
            float playerRotation,
            float targetRotation,
            bool isLegacyMoveMode,
            out Vector2 direction)
        {
            if (dist <= CenterEscapeThreshold)
            {
                direction = ComputeDesiredWorldDirection(radial, dist, playerRotation, RearHoldOrbitRadius, _spinSign, isLegacyMoveMode);
                return true;
            }

            float playerAngle = MathF.Atan2(radial.X, radial.Y);
            float rearTargetAngle = targetRotation + _spinSign * (RearHoldCenterDeg * (MathF.PI / 180f));
            float angleErr = WrapSignedAngle(rearTargetAngle - playerAngle);
            float deadbandRad = RearHoldDeadbandDeg * (MathF.PI / 180f);

            if (MathF.Abs(angleErr) <= deadbandRad && MathF.Abs(dist - RearHoldOrbitRadius) <= 0.45f)
            {
                direction = default;
                return false;
            }

            float relSigned = WrapSignedAngle(playerAngle - targetRotation);
            float moveSign = MathF.Abs(relSigned) < MathF.PI * 0.5f
                ? ChooseSpinSign(radial, targetRotation)
                : (angleErr >= 0f ? 1f : -1f);

            direction = ComputeDesiredWorldDirection(radial, dist, playerRotation, RearHoldOrbitRadius, moveSign, isLegacyMoveMode);
            return true;
        }

        private Vector2 ComputeDesiredWorldDirection(
            Vector2 radial,
            float dist,
            float playerRotation,
            float radius,
            float spinSign,
            bool isLegacyMoveMode)
        {
            if (dist < CenterEscapeThreshold)
            {
                if (_lastDirection.LengthSquared() > 1e-6f)
                    return Vector2.Normalize(_lastDirection);

                return new Vector2(MathF.Sin(playerRotation), MathF.Cos(playerRotation));
            }

            Vector2 radialUnit = radial / dist;
            // In FFXIV (X, Z) space where radialUnit = (sin(theta), cos(theta)),
            // (radialUnit.Y, -radialUnit.X) = d(radialUnit)/d(theta) increases theta.
            Vector2 tangent = spinSign * new Vector2(radialUnit.Y, -radialUnit.X);

            if (dist > radius + ApproachBlendBand)
            {
                // Smooth tangent-entry approach from any distance to the orbit circle.
                float sinAlpha = Math.Clamp(radius / dist, 0f, 1f);
                float cosAlpha = MathF.Sqrt(MathF.Max(0f, 1f - sinAlpha * sinAlpha));
                return Vector2.Normalize(cosAlpha * (-radialUnit) + sinAlpha * tangent);
            }

            if (dist > radius)
            {
                float inwardWeight = Math.Clamp((dist - radius) * RadialCorrectionGain, 0f, 1.5f);
                return Vector2.Normalize(tangent + inwardWeight * (-radialUnit));
            }

            if (dist < radius - 0.02f)
            {
                float outwardWeight = Math.Clamp((radius - dist) * OutwardCorrectionGain, 0f, MaxOutwardWeight);
                Vector2 candidate = Vector2.Normalize(tangent + outwardWeight * radialUnit);
                if (isLegacyMoveMode)
                    return candidate;

                Vector2 playerForward = new Vector2(MathF.Sin(playerRotation), MathF.Cos(playerRotation));
                if (Vector2.Dot(candidate, playerForward) >= 0.05f)
                    return candidate;
            }

            // On the orbit radius (or Standard mode facing inward): output pure tangent so Standard mode never backpedals.
            return tangent;
        }

        private void ReleaseMovement()
        {
            _movement.Release();
            _movement.CancelOnManualInput = false;
            _movement.ObserveUserInput = false;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            Stop();
            _movement.Dispose();
            _disposed = true;
        }
    }
}
