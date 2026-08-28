// P/Invoke surface of the private kperf / kperfdata frameworks (the machinery
// behind Instruments). Signatures follow the well-known kpc_demo.c (ibireme).
// Private API: may break on any macOS update; dev-machine diagnostics only.

using System.Runtime.InteropServices;

static class Kpc
{
    const string Kperf = "/System/Library/PrivateFrameworks/kperf.framework/kperf";
    const string KperfData = "/System/Library/PrivateFrameworks/kperfdata.framework/kperfdata";

    public const int MaxCounters = 32;

    // ---- kperf: kernel PMU control (root required for kpc_force_all_ctrs_set) ----
    [DllImport(Kperf)] public static extern int kpc_force_all_ctrs_set(int val);
    [DllImport(Kperf)] public static extern unsafe int kpc_set_config(uint classes, ulong* config);
    [DllImport(Kperf)] public static extern int kpc_set_counting(uint classes);
    [DllImport(Kperf)] public static extern int kpc_set_thread_counting(uint classes);
    [DllImport(Kperf)] public static extern unsafe int kpc_get_thread_counters(uint tid, uint bufCount, ulong* buf);

    // ---- kperfdata: event database (/usr/share/kpep/*.plist) and register mapping ----
    [DllImport(KperfData)] public static extern unsafe int kpep_db_create(byte* name, out IntPtr db);
    [DllImport(KperfData)] public static extern int kpep_config_create(IntPtr db, out IntPtr cfg);
    [DllImport(KperfData)] public static extern int kpep_config_force_counters(IntPtr cfg);
    [DllImport(KperfData)] public static extern unsafe int kpep_db_event(IntPtr db, byte* name, out IntPtr ev);
    [DllImport(KperfData)] public static extern unsafe int kpep_config_add_event(IntPtr cfg, ref IntPtr ev, uint flag, uint* err);
    [DllImport(KperfData)] public static extern unsafe int kpep_config_kpc(IntPtr cfg, ulong* buf, nuint bufSizeBytes);
    [DllImport(KperfData)] public static extern int kpep_config_kpc_count(IntPtr cfg, out nuint count);
    [DllImport(KperfData)] public static extern int kpep_config_kpc_classes(IntPtr cfg, out uint classes);
    [DllImport(KperfData)] public static extern unsafe int kpep_config_kpc_map(IntPtr cfg, nuint* buf, nuint bufSizeBytes);
}
