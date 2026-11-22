using System.IO.Compression;
using System.Numerics;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Core;
using MasterPlayer.MechanicRuntime;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Map;

internal readonly record struct FloorMapKey(uint Dungeon, int Set, uint Territory, int Layout)
{
    internal static unsafe FloorMapKey? For(InstanceContentDeepDungeon* dd)
    {
        var kind = DeepDungeonFloorClassifier.Classify(dd->DeepDungeonId, dd->Floor);
        if (kind == DeepDungeonFloorKind.Boss) return null;
        int layout = kind switch
        {
            DeepDungeonFloorKind.Result => 4,
            DeepDungeonFloorKind.Mob => FloorTerrainProfile.LayoutIndex(dd->LayoutInitializationType),
            _ => -1
        };
        return new(dd->DeepDungeonId, (dd->Floor - 1) / 10 + 1, Service.ClientState.TerritoryType, layout);
    }

    public override string ToString() => $"{Dungeon}-{Set}-{Territory}-{Layout}";
}

internal sealed class FloorMap
{
    internal static readonly NavigationClearance Clearance = new(FloorTerrainProfile.BakedBodyRadius, NavigationRange.MovementTolerance);
    internal readonly record struct Door(int Cell, int Connection, Matrix4x4 Box);

    private static FloorMapKey? cachedKey;
    private static FloorMap? cached;
    private static FloorMapKey? failedKey;
    private static long nextReadAt;
    private static (FloorMapKey Key, byte Floor, string Error)? terminalReadError;
    private static string? failedReadError;
    internal static bool Failed { get; private set; }
    internal static string Error { get; private set; } = "";
    internal readonly Vector3?[] Centers = new Vector3?[25];
    internal FloorMapKey Key { get; }
    internal readonly Door[] Doors;
    internal readonly int Width;
    internal readonly int Height;
    internal readonly Vector3 Origin;
    internal readonly float PixelSize;
    internal readonly byte[] Bits;
    internal readonly int RowBytes;
    internal string Source { get; }
    internal float SourcePixelSize { get; }

    internal static FloorMap? Get(FloorMapKey key, byte floor = 0)
    {
        Failed = false;
        if (key == cachedKey)
        {
            Error = "";
            return cached;
        }
        if (terminalReadError is { } terminal && key == terminal.Key && (floor == 0 || floor == terminal.Floor))
        {
            Error = terminal.Error;
            Failed = true;
            return null;
        }
        var live = FloorMapSource.ActiveLive;
        if (live != null && !live.Ensure(key, floor))
        {
            Error = live.Status;
            Failed = live.Failed;
            return null;
        }
        long now = Environment.TickCount64;
        if (key == failedKey && now < nextReadAt)
        {
            Error = failedReadError ?? "";
            return null;
        }
        Error = "";
        try
        {
            using var source = live == null ? FloorMapSource.OpenBuiltIn(key) : File.OpenRead(live.MapPath(key));
            using var stream = new GZipStream(source, CompressionMode.Decompress);
            using var reader = new BinaryReader(stream);
            cached = new FloorMap(key, reader, live == null ? "built-in" : "client-layout:vnav");
            cachedKey = key;
            failedKey = null;
            return cached;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            ReportError(key, ex.Message);
            if (live?.RejectMap(key) != true)
            {
                terminalReadError = (key, floor, Error);
                Failed = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportError(key, ex.Message);
            bool transient = ex is FileNotFoundException || ex is IOException && (ex.HResult & 0xffff) is 32 or 33;
            if (!transient)
            {
                terminalReadError = (key, floor, Error);
                Failed = true;
            }
        }
        failedKey = key;
        failedReadError = Error;
        nextReadAt = now + 1000;
        return null;
    }

    internal static void Reset()
    {
        cachedKey = null;
        failedKey = null;
        terminalReadError = null;
        cached = null;
        Error = "";
    }

    private static void ReportError(FloorMapKey key, string detail)
    {
        var status = $"地宮 {key.Dungeon}、第 {key.Set} 組、區域 {key.Territory}、版型 {key.Layout}：{detail}";
        if (key != failedKey || status != failedReadError) Service.Log.Error(status);
        Error = status;
    }

    private FloorMap(FloorMapKey key, BinaryReader reader, string source)
    {
        Key = key;
        Width = reader.ReadInt32();
        Height = reader.ReadInt32();
        Origin = new(reader.ReadSingle(), 0, reader.ReadSingle());
        PixelSize = reader.ReadSingle();
        var count = reader.ReadInt32();
        if (Width <= 0 || Height <= 0 || !float.IsFinite(Origin.X) || !float.IsFinite(Origin.Z) ||
            !float.IsFinite(PixelSize) || PixelSize <= 0 || count < 0 || count > Centers.Length)
            throw new InvalidDataException("底圖的尺寸或房間資料無效。");
        for (int i = 0; i < count; i++)
        {
            int cell = reader.ReadInt32();
            if ((uint)cell >= Centers.Length || Centers[cell].HasValue)
                throw new InvalidDataException("底圖的房間索引無效。");
            var center = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (!float.IsFinite(center.X) || !float.IsFinite(center.Y) || !float.IsFinite(center.Z))
                throw new InvalidDataException("底圖的房間座標無效。");
            Centers[cell] = center;
        }
        int doorCount = reader.ReadInt32();
        if (doorCount < 0) throw new InvalidDataException("底圖的門數量無效。");
        var doors = new List<Door>(Math.Min(doorCount, Centers.Length * 4));
        for (int i = 0; i < doorCount; i++)
        {
            int cell = reader.ReadInt32(), connection = reader.ReadInt32();
            if ((uint)cell >= Centers.Length || connection is not (1 or 2 or 4 or 8))
                throw new InvalidDataException("底圖的門索引或方向無效。");
            var m = Matrix4x4.Identity;
            m.M11 = reader.ReadSingle(); m.M13 = reader.ReadSingle();
            m.M21 = reader.ReadSingle(); m.M23 = reader.ReadSingle();
            m.M31 = reader.ReadSingle(); m.M33 = reader.ReadSingle();
            m.M41 = reader.ReadSingle(); m.M43 = reader.ReadSingle();
            doors.Add(new(cell, connection, m));
        }
        Doors = doors.ToArray();
        RowBytes = (int)(((Width + 31L) / 32) * 4);
        long byteCount = (long)RowBytes * Height;
        if (byteCount > Array.MaxLength) throw new InvalidDataException("底圖點陣尺寸超出可讀範圍。");
        var centers = new byte[(int)byteCount];
        reader.BaseStream.ReadExactly(centers);
        if (reader.BaseStream.ReadByte() != -1) throw new InvalidDataException("底圖含有未預期的資料。");
        Bits = centers;
        Source = source;
        SourcePixelSize = PixelSize;
        float bakedRadius = FloorTerrainProfile.BakedBodyRadius;
        int border = Clearance.WholeCellBorder(PixelSize, bakedRadius);
        for (int i = 0; i < border; ++i) Bits = WholeCellObstacles(Bits, RowBytes, Height);
    }

    private static byte[] WholeCellObstacles(byte[] centers, int stride, int height)
    {
        // FSD admits every point in a free cell. Dilate by the caller's border
        // to cover cell extent, follower tolerance and any unbaked body radius.
        // Do this once per loaded base map, before current-floor door overlays.
        var result = new byte[centers.Length];
        Array.Fill(result, (byte)0xff);
        for (int y = 1; y < height - 1; ++y)
            for (int x = 0; x < stride; ++x)
            {
                int i = y * stride + x;
                int column = centers[i - stride] | centers[i] | centers[i + stride];
                int before = x == 0 ? 0xff :
                    centers[i - stride - 1] | centers[i - 1] | centers[i + stride - 1];
                int after = x + 1 == stride ? 0xff :
                    centers[i - stride + 1] | centers[i + 1] | centers[i + stride + 1];
                result[i] = (byte)(column | (column << 1) | (column >> 1)
                    | ((before & 1) << 7) | ((after & 0x80) >> 7));
            }
        return result;
    }
}
