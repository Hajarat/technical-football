using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Godot;

namespace FootballSim.Game;

/// <summary>
/// --perf: per-frame cost of the running game. One row per rendered frame: the frame interval, the engine's process and
/// physics-process time, the main viewport's CPU and GPU render time, draw calls, objects and primitives, managed allocation
/// and GC collections, plus the round state and what changed. The engine's monitors and the render times describe the frame
/// before the one being read, which is also the frame the interval measures, so a row is one frame. Prints a summary at the end
/// and, with --perf-csv, writes the rows. --perf-vsync on|off sets the mode (on = vsync with max_fps 60, off = uncapped).
/// </summary>
public sealed partial class PerfProbe
{
    /// <summary>
    /// A node that stamps the clock at the start or end of the engine's process and physics-process passes: the first and last
    /// in the tree's processing order (process priority), so the span between them covers every node's _Process / _PhysicsProcess.
    /// (Performance.TimeProcess and TimePhysicsProcess only refresh about once a second.)
    /// </summary>
    sealed partial class Mark : Node
    {
        readonly PerfProbe _probe;
        readonly bool _end;
        public Mark(PerfProbe probe, bool end)
        {
            _probe = probe; _end = end;
            ProcessPriority = end ? int.MaxValue : int.MinValue;
            ProcessPhysicsPriority = end ? int.MaxValue : int.MinValue;
        }
        public override void _Process(double delta) { if (_end) _probe.ProcessEnd(); else _probe.ProcessStart(); }
        public override void _PhysicsProcess(double delta) { if (_end) _probe.PhysicsEnd(); else _probe.PhysicsStart(); }
    }

    public const int TickSlot = 0, PresentSlot = 1, AttackerSlot = 2, KeeperSlot = 3, DefenderSlot = 4;
    static readonly string[] SlotNames = { "tick_ms", "present_ms", "attacker_apply_ms", "keeper_apply_ms", "defender_apply_ms" };
    readonly ulong[] _slotUs = new ulong[5];
    /// <summary>Adds microseconds to a named section of the physics tick (summed over the ticks of one frame).</summary>
    public void Section(int slot, ulong us) => _slotUs[slot] += us;

    ulong _procStart, _procMs_us, _physStart, _physSum, _physTicks;
    void ProcessStart() => _procStart = Time.GetTicksUsec();
    void ProcessEnd() { _procMs_us = Time.GetTicksUsec() - _procStart; }
    void PhysicsStart() => _physStart = Time.GetTicksUsec();
    void PhysicsEnd() { _physSum += Time.GetTicksUsec() - _physStart; _physTicks++; }

    struct Row
    {
        public int Frame, Tick, PhysTicks, Gen0, Gen1, Gen2;
        public double S0, S1, S2, S3, S4;
        public double DeltaMs, ProcessMs, PhysicsMs, CpuMs, GpuMs, Draws, Objects, Prims;
        public long AllocBytes;
        public string State, Note;
    }

    readonly List<Row> _rows = new();
    readonly Rid _viewport;
    readonly string? _csv;
    readonly int _warmup;
    string _lastState = "";
    int _lastShots;
    bool _lastSkill;
    ulong _lastProcUs;
    int _g0, _g1, _g2;
    long _alloc;
    string _pendingNote = "";

    public PerfProbe(Node host, Viewport viewport, ToolArgs args)
    {
        host.AddChild(new Mark(this, false)); host.AddChild(new Mark(this, true));
        _viewport = viewport.GetViewportRid();
        _csv = args.PerfCsv;
        _warmup = args.PerfWarmup;
        RenderingServer.ViewportSetMeasureRenderTime(_viewport, true);
        if (args.PerfVsync is { } mode)
        {
            DisplayServer.WindowSetVsyncMode(mode ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
            Engine.MaxFps = mode ? 60 : 0;
        }
        if (args.PerfScale != 1f) viewport.Scaling3DScale = args.PerfScale;
        if (args.PerfMsaa >= 0) viewport.Msaa3D = args.PerfMsaa switch { 2 => Viewport.Msaa.Msaa2X, 4 => Viewport.Msaa.Msaa4X, _ => Viewport.Msaa.Disabled };
        GD.Print($"PERF_MODE vsync={DisplayServer.WindowGetVsyncMode()} max_fps={Engine.MaxFps} refresh_hz={DisplayServer.ScreenGetRefreshRate()} warmup={_warmup} renderer={RenderingServer.GetCurrentRenderingDriverName()} " +
                 $"adapter={RenderingServer.GetVideoAdapterName()} window={DisplayServer.WindowGetSize()} msaa3d={viewport.Msaa3D} scale3d={viewport.Scaling3DScale}");
        _g0 = GC.CollectionCount(0); _g1 = GC.CollectionCount(1); _g2 = GC.CollectionCount(2);
        _alloc = GC.GetAllocatedBytesForCurrentThread();
    }

    /// <summary>Marks something that happened this frame (it lands in the next row's note).</summary>
    public void Note(string text) => _pendingNote += (_pendingNote.Length > 0 ? "+" : "") + text;

    /// <summary>Called once per _Process, with the frame interval.</summary>
    public void Frame(double delta, int tick, string state, int shots, bool skill)
    {
        if (state != _lastState) { Note("state:" + state); _lastState = state; }
        if (shots != _lastShots) { Note("shot" + shots); _lastShots = shots; }
        if (skill && !_lastSkill) Note("skill");
        _lastSkill = skill;
        // This frame's own process pass is not finished yet, so the row carries the previous pass (the frame the interval measures);
        // the physics ticks are those run since the last row.
        ulong physUs = _physSum, physTicks = _physTicks; _physSum = 0; _physTicks = 0;
        _lastProcUs = _procMs_us;
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long alloc = GC.GetAllocatedBytesForCurrentThread();
        _rows.Add(new Row
        {
            Frame = _rows.Count, Tick = tick, DeltaMs = delta * 1000,
            ProcessMs = _lastProcUs / 1000.0,
            PhysicsMs = physUs / 1000.0, PhysTicks = (int)physTicks,
            CpuMs = RenderingServer.ViewportGetMeasuredRenderTimeCpu(_viewport),
            GpuMs = RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewport),
            Draws = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame),
            Objects = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalObjectsInFrame),
            Prims = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame),
            S0 = _slotUs[0] / 1000.0, S1 = _slotUs[1] / 1000.0, S2 = _slotUs[2] / 1000.0, S3 = _slotUs[3] / 1000.0, S4 = _slotUs[4] / 1000.0,
            AllocBytes = alloc - _alloc, Gen0 = g0 - _g0, Gen1 = g1 - _g1, Gen2 = g2 - _g2,
            State = state, Note = _pendingNote,
        });
        Array.Clear(_slotUs);
        _g0 = g0; _g1 = g1; _g2 = g2; _alloc = alloc; _pendingNote = "";
    }

    static string Stats(string name, IEnumerable<double> values, string unit = "ms")
    {
        var v = values.OrderBy(x => x).ToArray();
        if (v.Length == 0) return $"PERF {name} n=0";
        double P(double q) => v[Math.Min(v.Length - 1, (int)(v.Length * q))];
        return string.Create(CultureInfo.InvariantCulture, $"PERF {name,-9} avg={v.Average():F3} p50={P(0.5):F3} p99={P(0.99):F3} max={v[^1]:F3} {unit}");
    }

    public void Summary()
    {
        // Row 0's interval is the time since the scene started, not a frame; it stays out of both groups.
        var all = _rows.Skip(1).ToList();
        var warm = all.Take(_warmup).ToList();
        var steady = all.Skip(_warmup).ToList();
        GD.Print($"PERF_FRAMES total={all.Count} warmup={warm.Count} steady={steady.Count} gen0={_rows.Sum(r => r.Gen0)} gen1={_rows.Sum(r => r.Gen1)} gen2={_rows.Sum(r => r.Gen2)} " +
                 $"alloc_bytes_per_frame={(steady.Count > 0 ? steady.Average(r => (double)r.AllocBytes) : 0):F0}");
        foreach (var (label, set) in new[] { ("STEADY", steady), ("WARMUP", warm) })
        {
            GD.Print($"PERF_GROUP {label}");
            GD.Print(Stats("frame", set.Select(r => r.DeltaMs)));
            GD.Print(Stats("process", set.Select(r => r.ProcessMs)));
            GD.Print(Stats("physics", set.Select(r => r.PhysicsMs)));
            GD.Print(Stats("tick", set.Select(r => r.S0)));
            GD.Print(Stats("present", set.Select(r => r.S1)));
            GD.Print(Stats("att_apply", set.Select(r => r.S2)));
            GD.Print(Stats("kee_apply", set.Select(r => r.S3)));
            GD.Print(Stats("def_apply", set.Select(r => r.S4)));
            GD.Print(Stats("phys_ticks", set.Select(r => (double)r.PhysTicks), "ticks/frame"));
            GD.Print(Stats("other", set.Select(r => r.DeltaMs - r.ProcessMs - r.PhysicsMs)));
            GD.Print(Stats("cpu_render", set.Select(r => r.CpuMs)));
            GD.Print(Stats("gpu_render", set.Select(r => r.GpuMs)));
            GD.Print(Stats("draws", set.Select(r => r.Draws), "calls"));
            GD.Print(Stats("objects", set.Select(r => r.Objects), "n"));
            GD.Print(Stats("prims", set.Select(r => r.Prims), "n"));
            GD.Print($"PERF {label} frames_over_16.7ms={set.Count(r => r.DeltaMs > 16.7)} frames_over_8.3ms={set.Count(r => r.DeltaMs > 8.3)} of {set.Count}");
        }
        foreach (var r in all.OrderByDescending(r => r.DeltaMs).Take(10))
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"PERF_SPIKE frame={r.Frame} tick={r.Tick} delta={r.DeltaMs:F2} proc={r.ProcessMs:F2} phys={r.PhysicsMs:F2} cpu={r.CpuMs:F2} gpu={r.GpuMs:F2} alloc={r.AllocBytes} gen0={r.Gen0} state={r.State} note={r.Note}"));
        if (_csv != null)
        {
            var sb = new StringBuilder("frame,tick,delta_ms,process_ms,physics_ms,cpu_render_ms,gpu_render_ms,draws,objects,prims,phys_ticks,tick_ms,present_ms,attacker_apply_ms,keeper_apply_ms,defender_apply_ms,alloc_bytes,gen0,gen1,gen2,state,note\n");
            foreach (var r in _rows)
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{r.Frame},{r.Tick},{r.DeltaMs:F3},{r.ProcessMs:F3},{r.PhysicsMs:F3},{r.CpuMs:F3},{r.GpuMs:F3},{r.Draws},{r.Objects},{r.Prims},{r.PhysTicks},{r.S0:F3},{r.S1:F3},{r.S2:F3},{r.S3:F3},{r.S4:F3},{r.AllocBytes},{r.Gen0},{r.Gen1},{r.Gen2},{r.State},{r.Note}"));
            File.WriteAllText(_csv, sb.ToString());
            GD.Print("PERF_CSV " + _csv);
        }
    }
}
