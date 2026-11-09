using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ForzaOverlay.App.Services;

/// <summary>
/// 读取进程完整性级别（integrity level），用于判断本程序权限是否低于游戏。
/// 低完整性进程注册的全局热键，在更高完整性窗口处于前台时不会收到消息（UIPI 限制）。
/// </summary>
internal static class ProcessPrivilege
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const int BufferSize = 256;

    public static int GetProcessIdFromWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return 0;
        }

        _ = GetWindowThreadProcessId(hwnd, out uint processId);
        return (int)processId;
    }

    public static int GetIntegrityLevel(int processId)
    {
        if (processId <= 0)
        {
            return -1;
        }

        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return -1;
        }

        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out token))
            {
                return -1;
            }

            buffer = Marshal.AllocHGlobal(BufferSize);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, BufferSize, out _))
            {
                return -1;
            }

            IntPtr sid = Marshal.ReadIntPtr(buffer);
            if (sid == IntPtr.Zero)
            {
                return -1;
            }

            IntPtr countPointer = GetSidSubAuthorityCount(sid);
            if (countPointer == IntPtr.Zero)
            {
                return -1;
            }

            byte count = Marshal.ReadByte(countPointer);
            if (count == 0)
            {
                return -1;
            }

            IntPtr subAuthority = GetSidSubAuthority(sid, count - 1);
            return subAuthority == IntPtr.Zero ? -1 : Marshal.ReadInt32(subAuthority);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (token != IntPtr.Zero)
            {
                _ = CloseHandle(token);
            }

            _ = CloseHandle(process);
        }
    }

    public static int GetCurrentIntegrityLevel()
    {
        using Process current = Process.GetCurrentProcess();
        return GetIntegrityLevel(current.Id);
    }

    public static bool IsCurrentLowerThan(int otherProcessId)
    {
        int mine = GetCurrentIntegrityLevel();
        int other = GetIntegrityLevel(otherProcessId);

        if (mine < 0 || other < 0)
        {
            return false;
        }

        return mine < other;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, int length, out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, int index);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
