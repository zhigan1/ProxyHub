using System.Runtime.InteropServices;

namespace ProxyHub.Adapters;

/// <summary>
/// state.vscdb 只读读取器（VSCode fork 的 SQLite 键值缓存，ItemTable 单表）。
/// 经 Windows 内置 winsqlite3.dll P/Invoke 实现（零 NuGet 依赖）；只读打开，
/// win32 VFS 默认共享模式容忍桌面端（FileShare.ReadWrite）同时占用。
/// winsqlite3 不可用（非 Windows）/文件缺失/损坏/被锁时一律返回空列表，绝不抛出——调用方安全降级。
/// 注意：仅按后缀选取调用方指定的键，不读取其他任何键值（不触及凭据类数据）。
/// </summary>
public static class StateVscDb
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteOpenReadonly = 0x00000001;

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(IntPtr filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr db, IntPtr sql, int nByte, out IntPtr stmt, IntPtr tail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(IntPtr db);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_busy_timeout(IntPtr db, int ms);

    /// <summary>
    /// 读取 ItemTable 中以任一 keySuffixes 结尾的键值对（LIKE 前缀通配 + C# 精确后缀复核，
    /// 键名的数字前缀/分隔符随 IDE 版本漂移不做硬编码）。任何异常返回已读部分或空列表。
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ReadItemTable(string dbPath, params string[] keySuffixes)
    {
        var rows = new List<KeyValuePair<string, string>>();
        if (keySuffixes is not { Length: > 0 }) return rows;

        IntPtr db = IntPtr.Zero, stmt = IntPtr.Zero, pathPtr = IntPtr.Zero, sqlPtr = IntPtr.Zero;
        try
        {
            pathPtr = Marshal.StringToCoTaskMemUTF8(dbPath);
            if (pathPtr == IntPtr.Zero) return rows;
            if (sqlite3_open_v2(pathPtr, out db, SqliteOpenReadonly, IntPtr.Zero) != SqliteOk) return rows;
            sqlite3_busy_timeout(db, 300); // 桌面端短暂持锁时让步，而非立即失败

            // LIKE 后缀通配把候选收窄到个位数行；'_' 是 LIKE 通配符无妨，C# 侧再精确复核
            var where = string.Join(" OR ", keySuffixes.Select(s => $"key LIKE '%{s.Replace("'", "''")}'"));
            sqlPtr = Marshal.StringToCoTaskMemUTF8($"SELECT key, value FROM ItemTable WHERE {where}");
            if (sqlite3_prepare_v2(db, sqlPtr, -1, out stmt, IntPtr.Zero) != SqliteOk) return rows;

            while (sqlite3_step(stmt) == SqliteRow)
            {
                var key = Marshal.PtrToStringUTF8(sqlite3_column_text(stmt, 0));
                var value = Marshal.PtrToStringUTF8(sqlite3_column_text(stmt, 1));
                if (key is null || value is null) continue;
                if (keySuffixes.Any(s => key.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                    rows.Add(new KeyValuePair<string, string>(key, value));
            }
            return rows;
        }
        catch
        {
            // winsqlite3 不可用（非 Windows）/文件异常：安全降级为空，由调用方回退静态基线
            return rows;
        }
        finally
        {
            if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
            if (db != IntPtr.Zero) sqlite3_close_v2(db);
            if (pathPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(pathPtr);
            if (sqlPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(sqlPtr);
        }
    }
}
