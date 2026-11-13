using System.Runtime.CompilerServices;

// Repro: Mono llvm-only AOT (browser-wasm) keeps a reference passed as an argument on no shadow-stack slot.
//
// A young object (in the nursery) is reachable only from the heap (a closure's field). A lambda loads it from the
// field and passes it straight to a getter; the getter allocates (a minor GC can run there) and then reads `this`
// again. Nothing pins the object conservatively, so the GC moves it and fixes the closure's field - but the getter's
// `this`, a wasm local, still holds the old address.
//
// args: <rounds> <mode>   mode = build | holder | both (default both)
//   build:  the Chart's own Build() makes the closures (one display class with <>4__this and a captured local);
//           the lambda `() => Summary` passes the loaded <>4__this on without spilling it. Crashes.
//   holder: an old Holder's field holds the young Chart; the lambda `() => holder.C!.Summary` has two loads and
//           spills the intermediate one. Does not crash - the difference is only that spill.
public sealed class Chart
{
    private static readonly Func<double> NoTurn = () => double.NaN;
    public Func<double> Turn = NoTurn;           // a delegate read through `this` after allocations
    public string Label;
    public double OwnTurn;
    public int[] Values;

    public Chart(string label, double ownTurn, int[] values) { Label = label; OwnTurn = ownTurn; Values = values; }

    private double TurnNow => double.IsNaN(Turn()) ? OwnTurn : Turn();

    // Two closures from one scope: C# puts `this` and `count` in one display class.
    public (Func<string> Said, Func<int> Count) Build(int count) => (() => Summary, () => count + Values.Length);

    // Allocations first (concat array, lambda, Join), `this` read between them and after them.
    public string Summary => Label + ": " + string.Join("; ", Values.Select(v => Label + " " + v))
        + ". Turned " + Math.Round(TurnNow) + "°";
}

public sealed class Holder { public Chart? C; }

public static class Program
{
    public static int Main(string[] args)
    {
        int rounds = args.Length > 0 ? int.Parse(args[0]) : 200_000;
        string mode = args.Length > 1 ? args[1] : "both";
        var holder = new Holder();
        Func<string> said = () => holder.C!.Summary;          // made once: old by the time it runs
        var values = new[] { 12, 18, 9, 22 };
        GC.Collect();                                          // holder, said, values: old
        int wrong = 0;
        for (int i = 0; i < rounds; i++)
        {
            double turn = -(i % 180);
            string expected = Expected(i, turn);
            if (mode is "holder" or "both")
            {
                SetChart(holder, i, turn, values);             // young, only the old holder holds it
                Scrub();
                if (!Check(i, "holder", said, expected, ref wrong)) return 2;
            }
            if (mode is "build" or "both")
            {
                var built = MakeSaid(i, turn, values);         // young chart, young closure, only the delegate holds them
                Scrub();
                if (!Check(i, "build", built, expected, ref wrong)) return 2;
            }
        }
        Console.WriteLine(wrong == 0 ? $"REPRO OK: {rounds} rounds ({mode}), no wrong summary" : $"REPRO WRONG: {wrong} wrong summaries in {rounds} rounds ({mode})");
        return wrong == 0 ? 0 : 1;
    }

    private static bool Check(int i, string how, Func<string> said, string expected, ref int wrong)
    {
        string s;
        try { s = said(); }
        catch (Exception ex) { Console.WriteLine($"REPRO {how} round {i}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); return false; }
        if (s != expected && ++wrong <= 5) Console.WriteLine($"REPRO {how} round {i}: got \"{s}\", expected \"{expected}\"");
        return true;
    }

    // The frame that made the chart must be gone: its shadow-stack slot would pin the chart.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SetChart(Holder holder, int i, double turn, int[] values) => holder.C = Make(i, turn, values);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Func<string> MakeSaid(int i, double turn, int[] values) => Make(i, turn, values).Build(i).Said;

    // Clears the shadow stack below us: a stale copy of the chart's address there would pin it conservatively.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Scrub() { Span<byte> s = stackalloc byte[8192]; s.Clear(); Keep(s); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Keep(Span<byte> s) { }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Chart Make(int i, double turn, int[] values) => new("Chart " + i, turn, values);

    private static string Expected(int i, double turn) =>
        $"Chart {i}: Chart {i} 12; Chart {i} 18; Chart {i} 9; Chart {i} 22. Turned {Math.Round(turn)}°";
}
