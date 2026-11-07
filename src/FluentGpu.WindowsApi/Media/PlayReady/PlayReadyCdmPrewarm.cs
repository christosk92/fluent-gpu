using System;
using System.Diagnostics;
using System.Threading;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>
/// Pays the PlayReady CDM's once-per-process initialisation off the critical path of the first protected open.
/// <para>Measured (2026-11-07, Snapdragon X Elite, cdmprobe): the FIRST <c>IMFMediaEngineClassFactory4::CreateContentDecryptionModuleFactory</c>
/// of a process costs 1181-1205 ms wall and 1125-1156 ms CPU on one thread (it loads 23 modules and runs PlayReady's load-time
/// initialisation), for the hardware (<c>.3000</c>) and the software key system alike; every later call in the same process costs
/// 1-77 ms. The native runtime's bring-up makes that call first thing (<c>CreateAndPrepareCdm</c>), so without this a cold first video
/// waits ~1.2 s on it, which is 55-60 % of a cold click-to-first-frame. Chromium creates and caches its CDM factory once per process
/// (<c>media_foundation_cdm_module.cc</c>); Firefox queries every key system's capabilities on a background queue at startup and caches
/// them (<c>MFCDMParent::GetAllKeySystemsCapabilities</c>).</para>
/// <para>The prewarm makes exactly the calls the bring-up's SL3000 probe makes (factory, then <c>IsTypeSupported</c> for H.264) on a
/// below-normal background thread, then releases everything: the bring-up itself still negotiates the key system, so its outcome is
/// unchanged; only the one-time cost has already been paid. It never throws and never blocks the caller.</para>
/// </summary>
internal static unsafe class PlayReadyCdmPrewarm
{
    private const string HardwareKeySystem = "com.microsoft.playready.recommendation.3000";
    private const string SoftwareKeySystem = "com.microsoft.playready.recommendation";
    private const string H264Type = "video/mp4;codecs=\"avc1.640028\"";

    private static readonly Guid ClsidMediaEngineClassFactory = new(0xb44392da, 0x499b, 0x446b, 0xa4, 0xcb, 0x00, 0x5f, 0xea, 0xd0, 0xe6, 0xd5);

    private static int s_started;

    /// <summary>Start the prewarm once per process (later calls return at once). <paramref name="softwareOnly"/> warms only the SL2000
    /// key system (the host forced SL2000, so the bring-up never asks for the hardware one).</summary>
    public static void Start(bool softwareOnly)
    {
        if (Interlocked.Exchange(ref s_started, 1) != 0) return;
        var t = new Thread(() => Run(softwareOnly)) { IsBackground = true, Name = "playready-prewarm", Priority = ThreadPriority.BelowNormal };
        t.Start();
    }

    private static void Run(bool softwareOnly)
    {
        long t0 = Stopwatch.GetTimestamp();
        bool com = false, mf = false;
        IMFMediaEngineClassFactory* baseFactory = null;
        IMFMediaEngineClassFactory4* factory4 = null;
        string outcome = "failed";
        try
        {
            HRESULT hr = CoInitializeEx(null, (uint)COINIT.COINIT_MULTITHREADED);
            com = hr.SUCCEEDED;
            if (hr.FAILED && hr != RPC.RPC_E_CHANGED_MODE) { outcome = $"CoInitializeEx hr=0x{(uint)hr.Value:X8}"; return; }
            hr = MFStartup(MF.MF_VERSION, MFSTARTUP_FULL);
            if (hr.FAILED) { outcome = $"MFStartup hr=0x{(uint)hr.Value:X8}"; return; }
            mf = true;
            Guid clsid = ClsidMediaEngineClassFactory;
            Guid iidBase = __uuidof<IMFMediaEngineClassFactory>();
            hr = CoCreateInstance(&clsid, null, (uint)CLSCTX.CLSCTX_INPROC_SERVER, &iidBase, (void**)&baseFactory);
            if (hr.FAILED || baseFactory == null) { outcome = $"CoCreateInstance hr=0x{(uint)hr.Value:X8}"; return; }
            Guid iid4 = __uuidof<IMFMediaEngineClassFactory4>();
            hr = baseFactory->QueryInterface(&iid4, (void**)&factory4);
            if (hr.FAILED || factory4 == null) { outcome = $"QI factory4 hr=0x{(uint)hr.Value:X8}"; return; }
            if (!softwareOnly && Warm(factory4, HardwareKeySystem)) { outcome = "SL3000 key system warm"; return; }
            outcome = Warm(factory4, SoftwareKeySystem) ? "SL2000 key system warm" : "no PlayReady key system";
        }
        catch (Exception e) { outcome = "threw " + e.GetType().Name; }
        finally
        {
            if (factory4 != null) factory4->Release();
            if (baseFactory != null) baseFactory->Release();
            if (mf) MFShutdown();
            if (com) CoUninitialize();
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            ProtectedVideoRuntime.WriteVideoLine($"cdm.prewarm {outcome} ms={ms:0}");
        }
    }

    /// <summary>The SL3000 probe's first two steps (PrRuntime.cpp CreateAndPrepareCdm): the key system's factory and H.264 support.</summary>
    private static bool Warm(IMFMediaEngineClassFactory4* factory4, string keySystem)
    {
        IMFContentDecryptionModuleFactory* cdmFactory = null;
        Guid iid = __uuidof<IMFContentDecryptionModuleFactory>();
        fixed (char* ks = keySystem)
        fixed (char* type = H264Type)
        {
            HRESULT hr = factory4->CreateContentDecryptionModuleFactory(ks, &iid, (void**)&cdmFactory);
            if (hr.FAILED || cdmFactory == null) return false;
            try { return cdmFactory->IsTypeSupported(ks, type) != 0; }
            finally { cdmFactory->Release(); }
        }
    }
}
