using System.Runtime.InteropServices;

namespace SimpleSoundpad;

static class GlobalHotkeys
{
    public const int FirstId = 2000, MessageId = 0x0312;
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr window, int id);
    public static bool Register(IntPtr window, int slot) => RegisterHotKey(window, FirstId + slot, 0x4003, (uint)Keys.D1 + (uint)slot);
    public static string Name(int slot) => $"Ctrl+Alt+{slot + 1}";
    public static void Check()
    {
        var lines = new List<string>();
        var registered = new List<int>();
        try
        {
            for (int i = 0; i < 5; i++)
            {
                if (!Register(IntPtr.Zero, i)) throw new Exception($"{Name(i)} unavailable, Windows error {Marshal.GetLastWin32Error()}");
                registered.Add(i);
                if (RegisterHotKey(IntPtr.Zero, FirstId + 50 + i, 0x4003, (uint)Keys.D1 + (uint)i))
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
