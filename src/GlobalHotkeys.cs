using System.Runtime.InteropServices;

namespace SimpleSoundpad;

static class GlobalHotkeys
{
    public const int FirstId = 2000, MessageId = 0x0312, StopSlot = 5, SlotCount = 6;
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr window, int id);
    static uint Key(int slot) => slot == StopSlot ? (uint)Keys.C : (uint)Keys.D1 + (uint)slot;
    public static bool Register(IntPtr window, int slot) => RegisterHotKey(window, FirstId + slot, 0x4003, Key(slot));
    public static string Name(int slot) => slot == StopSlot ? "Ctrl+Alt+C" : $"Ctrl+Alt+{slot + 1}";
    public static void Check()
    {
        var lines = new List<string>();
        var registered = new List<int>();
        try
        {
            for (int i = 0; i < SlotCount; i++)
            {
                if (!Register(IntPtr.Zero, i)) throw new Exception($"{Name(i)} unavailable, Windows error {Marshal.GetLastWin32Error()}");
                registered.Add(i);
                if (RegisterHotKey(IntPtr.Zero, FirstId + 50 + i, 0x4003, Key(i)))
                {
                    UnregisterHotKey(IntPtr.Zero, FirstId + 50 + i);
                    throw new Exception("Duplicate shortcut unexpectedly registered");
                }
                lines.Add($"REGISTER / CONFLICT DETECTION OK: {Name(i)}");
            }
        }
        catch (Exception e) { lines.Add("FAILED: " + e.Message); }
        finally { foreach (int i in registered) UnregisterHotKey(IntPtr.Zero, FirstId + i); }
        File.WriteAllLines(Path.Combine(Program.Home, "hotkey-check.txt"), lines);
    }
}
