using System;
using System.Numerics;
using System.Runtime.InteropServices;
using MasterPlayer.MechanicRuntime;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>Read the local MoveController's registered knockback, not a
/// guessed elapsed timer. Layout follows the7.58 register/update consumers.</summary>
public static unsafe class NativeDisplacementReader
{
    [StructLayout(LayoutKind.Explicit, Size = 0x64)]
    private struct MotionRecord
    {
        [FieldOffset(0)] public Vector3 Target;
        [FieldOffset(0x24)] public float Duration;
        [FieldOffset(0x2C)] public int Mode;
        [FieldOffset(0x3F)] public byte Active;
        [FieldOffset(0x54)] public float Distance;
        [FieldOffset(0x58)] public float Progress;
        [FieldOffset(0x60)] public byte Curve;
    }

    public static NativeDisplacementObservation? Capture(Character* character, DateTime now)
    {
        if (character == null) return null;
        var move = &character->MoveController;
        if (move->OwnerObject != (BattleChara*)character) return null;
        //14182C14E passes MoveController+1C0 to14181BD90. The native
        //14181AD10 gate and14181B6D0 progress update consume these fields.
        var record = *(MotionRecord*)((byte*)move + 0x1C0);
        if (record.Active == 0 || record.Mode != 3 || record.Curve != 3
            || !float.IsFinite(record.Duration) || record.Duration <= 0
            || !float.IsFinite(record.Progress) || record.Progress < 0 || record.Progress >= 1
            || !float.IsFinite(record.Distance) || record.Distance < 0
            || !float.IsFinite(record.Target.X) || !float.IsFinite(record.Target.Z)) return null;
        return NativeDisplacementObservation.FromNativeRecord(new(record.Target.X, record.Target.Z),
            now, record.Duration, record.Progress, record.Distance);
    }
}
