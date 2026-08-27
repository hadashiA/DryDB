// Configures the PMU for the event set below and reads the calling thread's
// virtualized counters. Setup sequence: resolve events in the kpep database,
// let kpep assign them to physical counters, write the register config through
// kpc, then enable per-thread counting (root required from kpc_force_all_ctrs_set
// on). Counter i of Events is read back through map[i], the physical register
// index kpep chose.

using System.Text;

static unsafe class Pmc
{
    static readonly string[] Events = PmcCounters.EventNames;

    static readonly nuint[] map = new nuint[Events.Length];

    public static void Init()
    {
        Check(Kpc.kpep_db_create(null, out var db), "kpep_db_create");
        Check(Kpc.kpep_config_create(db, out var cfg), "kpep_config_create");
        Check(Kpc.kpep_config_force_counters(cfg), "kpep_config_force_counters");

        foreach (var name in Events)
        {
            var utf8 = Encoding.UTF8.GetBytes(name + "\0");
            fixed (byte* p = utf8)
            {
                Check(Kpc.kpep_db_event(db, p, out var ev), $"kpep_db_event({name})");
                uint err = 0;
                Check(Kpc.kpep_config_add_event(cfg, ref ev, 0, &err), $"kpep_config_add_event({name}) err={err}");
            }
        }

        Check(Kpc.kpep_config_kpc_classes(cfg, out var classes), "kpep_config_kpc_classes");
        Check(Kpc.kpep_config_kpc_count(cfg, out var regCount), "kpep_config_kpc_count");
        fixed (nuint* m = map)
        {
            Check(Kpc.kpep_config_kpc_map(cfg, m, (nuint)(map.Length * sizeof(nuint))), "kpep_config_kpc_map");
        }
        var regs = stackalloc ulong[Kpc.MaxCounters];
        Check(Kpc.kpep_config_kpc(cfg, regs, regCount * sizeof(ulong)), "kpep_config_kpc");

        // Root required from here on.
        Check(Kpc.kpc_force_all_ctrs_set(1), "kpc_force_all_ctrs_set (run with sudo?)");
        Check(Kpc.kpc_set_config(classes, regs), "kpc_set_config");
        Check(Kpc.kpc_set_counting(classes), "kpc_set_counting");
        Check(Kpc.kpc_set_thread_counting(classes), "kpc_set_thread_counting");
    }

    /// <summary>Reads the calling thread's counters into one <see cref="PmcCounters"/> sample.</summary>
    public static PmcCounters Read()
    {
        var buf = stackalloc ulong[Kpc.MaxCounters];
        var ret = Kpc.kpc_get_thread_counters(0, Kpc.MaxCounters, buf);
        if (ret != 0) throw new Exception($"kpc_get_thread_counters failed: {ret}");

        var values = default(PmcCounters);
        for (var i = 0; i < PmcCounters.EventCount; i++)
        {
            values[i] = buf[map[i]];
        }
        return values;
    }

    static void Check(int ret, string what)
    {
        if (ret != 0) throw new Exception($"{what} failed: {ret}");
    }
}
