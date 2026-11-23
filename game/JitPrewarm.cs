using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace FootballSim.Game;

/// <summary>
/// Compiles the game's own code ahead of use. .NET compiles a method the first time it runs, so the first live tick of a round
/// (the simulation's whole live path: a few hundred methods) and the first touch, shot and save each took a burst of compiling
/// in the middle of play. This compiles every method of the simulation, animation and game assemblies on a background thread
/// while the scene loads; <see cref="Wait"/> holds round setup until it is done. Compiling runs no code and changes no state:
/// static constructors and field initialisers still run when the code first uses them, as before.
/// </summary>
public static class JitPrewarm
{
    static Thread? _thread;
    /// <summary>Methods compiled and the time taken, once done.</summary>
    public static int Methods { get; private set; }
    public static double Milliseconds { get; private set; }

    /// <summary>Starts compiling in the background (once).</summary>
    public static void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "JitPrewarm", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Blocks until the background compile has finished (at once if it never started or is done).</summary>
    public static void Wait() => _thread?.Join();

    static void Run()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int n = 0;
        foreach (var asm in new[] { typeof(JitPrewarm).Assembly, typeof(FootballSim.Core.World).Assembly, typeof(FootballSim.Anim.Clip).Assembly })
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = Array.FindAll(e.Types, t => t != null)!; }
            const BindingFlags All = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var type in types)
            {
                if (type.ContainsGenericParameters) continue;
                foreach (var m in type.GetMethods(All)) n += Prepare(m);
                foreach (var c in type.GetConstructors(All)) n += Prepare(c);
            }
        }
        Methods = n;
        Milliseconds = watch.Elapsed.TotalMilliseconds;
    }

    static int Prepare(MethodBase m)
    {
        if (m.IsAbstract || m.ContainsGenericParameters || m.IsStatic && m.IsConstructor) return 0;
        if ((m.MethodImplementationFlags & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime)) != 0 || (m.Attributes & MethodAttributes.PinvokeImpl) != 0) return 0;
        try { RuntimeHelpers.PrepareMethod(m.MethodHandle); return 1; }
        catch (Exception) { return 0; }   // (a method the runtime cannot prepare ahead is simply compiled on first use, as before)
    }
}
