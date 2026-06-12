// Minimal CoreCLR working-set floor probe. Mirrors the engine's measurement exactly:
// Environment.WorkingSet (the same value MemCensus.cs:146 prints). No window, no GUI.
using System.Globalization;

Thread.Sleep(1500);   // let startup/tiered-JIT background work settle
var gc = GC.GetGCMemoryInfo();
long ws = Environment.WorkingSet;
long priv = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"floor-console ws={ws} ({ws / (1024.0 * 1024.0):0.0}MB) priv={priv} ({priv / (1024.0 * 1024.0):0.0}MB) gcCommitted={gc.TotalCommittedBytes} ({gc.TotalCommittedBytes / (1024.0 * 1024.0):0.0}MB)"));
