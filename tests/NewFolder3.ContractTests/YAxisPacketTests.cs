using System.Numerics;

namespace NewFolder3;

internal static unsafe class YAxisPacketTests
{
    public static void Run()
    {
        byte* packet = stackalloc byte[80];
        new Span<byte>(packet, 80).Fill(0xAB);
        var player = new Vector3(100, 20, 200);
        *(Vector3*)(packet + 40) = player;
        byte[] before = new Span<byte>(packet, 80).ToArray();
        Check(YAxisPacketAdjustment.TryAdjust((nint)packet, false, -7f, player), "Overworld adjustment rejected.");
        Check(*(Vector3*)(packet + 40) == new Vector3(100, 13, 200), "Overworld coordinates are incorrect.");
        for (int i = 0; i < 80; i++)
            if (i < 44 || i >= 48) Check(packet[i] == before[i], "Overworld adjustment changed unrelated bytes.");

        *(Vector3*)(packet + 44) = player;
        *(Vector3*)(packet + 56) = player + new Vector3(1, 1, 1);
        before = new Span<byte>(packet, 80).ToArray();
        Check(YAxisPacketAdjustment.TryAdjust((nint)packet, true, 11f, player), "Instance adjustment rejected.");
        Check(*(Vector3*)(packet + 44) == new Vector3(100, 31, 200), "Instance position is incorrect.");
        Check(*(Vector3*)(packet + 56) == new Vector3(101, 31, 201), "Instance second position is incorrect.");
        for (int i = 0; i < 80; i++)
            if ((i < 48 || i >= 52) && (i < 60 || i >= 64))
                Check(packet[i] == before[i], "Instance adjustment changed unrelated bytes.");

        *(Vector3*)(packet + 40) = player;
        Check(!YAxisPacketAdjustment.TryAdjust((nint)packet, false, float.NaN, player), "Invalid delta was accepted.");
        Check(!YAxisPacketAdjustment.TryAdjust((nint)packet, false, 0f, player), "Zero delta was accepted.");
        Check(YAxisPacketAdjustment.TryAdjust((nint)packet, false, 99f, player) &&
            ((Vector3*)(packet + 40))->Y == 35f, "Delta was not clamped.");
        *(Vector3*)(packet + 40) = new Vector3(float.NaN, 20, 200);
        Check(!YAxisPacketAdjustment.TryAdjust((nint)packet, false, -7f, player), "Invalid position was accepted.");
        *(Vector3*)(packet + 40) = player + new Vector3(100, 0, 0);
        Check(!YAxisPacketAdjustment.TryAdjust((nint)packet, false, -7f, player), "Distant position was accepted.");
        Check(!YAxisPacketAdjustment.TryAdjust(0, false, -7f, player), "Null packet was accepted.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
