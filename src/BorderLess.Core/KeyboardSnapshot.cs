namespace BorderLess.Core;

// No characters or keyboard layout are transmitted here. This is the state of
// six modifiers and three toggle keys, carried with each keyboard event.
public readonly record struct KeyboardSnapshot(int Modifiers, int Locks);

public static class KeyboardPacket
{
    public const int NativeMask = 0x1ffff;
    private const int SnapshotFlag = 1 << 17;
    private const int AllowedMask = 0x07ffffff;
    public static readonly int[] ModifierKeys = { 0xa0, 0xa1, 0xa2, 0xa3, 0xa4, 0xa5 };
    public static readonly int[] ToggleKeys = { 0x14, 0x90, 0x91 };
    public static int Normalize(int key, int nativeData) => key switch
    {
        0x10 => (nativeData & 0xffff) == 0x36 ? 0xa1 : 0xa0,
        0x11 => (nativeData & 0x10000) != 0 ? 0xa3 : 0xa2,
        0x12 => (nativeData & 0x10000) != 0 ? 0xa5 : 0xa4,
        _ => key
    };
    public static int ModifierBit(int key)
    {
        var index = Array.IndexOf(ModifierKeys, key);
        return index < 0 ? 0 : 1 << index;
    }
    public static int ToggleBit(int key)
    {
        var index = Array.IndexOf(ToggleKeys, key);
        return index < 0 ? 0 : 1 << index;
    }
    public static bool IsStateKey(int key) => ModifierBit(key) != 0 || ToggleBit(key) != 0;
    public static int CanonicalData(int key) => key switch
    {
        0xa0 => 0x2a, 0xa1 => 0x36, 0xa2 => 0x1d, 0xa3 => 0x1001d,
        0xa4 => 0x38, 0xa5 => 0x10038, 0x14 => 0x3a, 0x90 => 0x10045, 0x91 => 0x46,
        _ => 0
    };
    public static int Pack(int nativeData, KeyboardSnapshot state) => (nativeData & NativeMask) | SnapshotFlag | ((state.Modifiers & 63) << 18) | ((state.Locks & 7) << 24);
    public static bool TryUnpack(int data, out int nativeData, out KeyboardSnapshot? state)
    {
        nativeData = data & NativeMask;
        state = null;
        if (data < 0 || (data & ~AllowedMask) != 0) return false;
        if ((data & SnapshotFlag) == 0) return data <= NativeMask;
        state = new KeyboardSnapshot((data >> 18) & 63, (data >> 24) & 7);
        return true;
    }
}
