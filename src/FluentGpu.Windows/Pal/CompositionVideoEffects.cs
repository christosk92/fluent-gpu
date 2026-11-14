using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using TerraFX.Interop.Windows;
using TerraFX.Interop.WinRT;
using static TerraFX.Interop.Windows.Windows;
using static TerraFX.Interop.WinRT.WinRT;

namespace FluentGpu.Pal.Windows;

/// <summary>
/// The effect graph of <see cref="CompositionVideoPresenter"/>'s frosted plates, described to <c>ICompositor::CreateEffectFactoryWithProperties</c>:
/// <c>Lum(LinearTransfer) ← Blur(GaussianBlur, border HARD) ← "Backdrop"</c> (a <c>CompositionEffectSourceParameter</c>; each plate's brush
/// binds a <c>CompositionBackdropBrush</c> to it). The compositor reads a graph only through <c>IGraphicsEffect</c> /
/// <c>IGraphicsEffectSource</c> and the interop <c>IGraphicsEffectD2D1Interop</c> (the D2D effect's CLSID, its properties as
/// <c>IPropertyValue</c>s, its sources), so the two effects and the list of animatable property names are hand-rolled COM objects over
/// native memory: static vtables of <c>[UnmanagedCallersOnly]</c> thunks and a real ref count, freed by whoever releases last (the
/// <c>VideoMediaEngine</c> notify pattern). They live only while the factory is created: <see cref="CreatePlateFactory"/> releases them.
/// <para>Animatable (written per plate through the brush's <c>Properties</c>, never a brush rebuild): <see cref="BlurAmount"/> and the
/// luminosity step's per-channel <see cref="LumSlopes"/> and <see cref="LumOffsets"/>. The luminosity step is a LinearTransfer:
/// <c>out.rgb = in.rgb * slope + offset</c>, alpha untouched (disabled), output clamped.</para>
/// </summary>
internal static unsafe class CompositionVideoEffects
{
    /// <summary>The source parameter each plate's brush binds its backdrop brush to.</summary>
    internal const string BackdropSource = "Backdrop";
    /// <summary>The blur's standard deviation (device px, D2D's StandardDeviation).</summary>
    internal const string BlurAmount = "Blur.BlurAmount";
    internal static readonly string[] LumSlopes = { "Lum.RedSlope", "Lum.GreenSlope", "Lum.BlueSlope" };
    internal static readonly string[] LumOffsets = { "Lum.RedOffset", "Lum.GreenOffset", "Lum.BlueOffset" };

    private static readonly Guid ClsidGaussianBlur = new(0x1feb6d69, 0x2fe6, 0x4ac9, 0x8c, 0x58, 0x1d, 0x7f, 0x93, 0xe7, 0xa6, 0xa5);
    private static readonly Guid ClsidLinearTransfer = new(0xad47c8fd, 0x63ef, 0x4acc, 0x9b, 0x51, 0x67, 0x97, 0x9c, 0x03, 0x6c, 0x06);

    /// <summary>The plate graph's factory with its animatable properties, or a failing HRESULT (every object of the graph released either
    /// way). <paramref name="status"/> is the factory's load status (Success unless the compositor says otherwise).</summary>
    internal static HRESULT CreatePlateFactory(ICompositor* comp, ICompositionEffectFactory** factory, CompositionEffectFactoryLoadStatus* status)
    {
        *factory = null;
        *status = CompositionEffectFactoryLoadStatus.CompositionEffectFactoryLoadStatus_Other;
        IPropertyValueStatics* pv = null;
        ICompositionEffectSourceParameterFactory* spf = null;
        ICompositionEffectSourceParameter* sp = null;
        IGraphicsEffectSource* backdrop = null;
        EffectCcw* blur = null, lum = null;
        StringListCcw* names = null;
        HRESULT hr = Activation("Windows.Foundation.PropertyValue", __uuidof<IPropertyValueStatics>(), (void**)&pv);
        if (Succeeded(hr)) hr = Activation("Windows.UI.Composition.CompositionEffectSourceParameter", __uuidof<ICompositionEffectSourceParameterFactory>(), (void**)&spf);
        if (Succeeded(hr))
        {
            fixed (char* p = BackdropSource)
            {
                HSTRING_HEADER h;
                HSTRING s;
                hr = WindowsCreateStringReference(p, (uint)BackdropSource.Length, &h, &s);
                if (Succeeded(hr)) hr = spf->Create(s, &sp);
            }
        }
        if (Succeeded(hr)) hr = ((IUnknown*)sp)->QueryInterface(__uuidof<IGraphicsEffectSource>(), (void**)&backdrop);
        // GaussianBlur: StandardDeviation, Optimization (BALANCED), BorderMode (HARD: the edge clamps, so the plate's rim does not darken
        // toward transparent black).
        Prop* bp = stackalloc Prop[] { Prop.F(0f), Prop.U(1), Prop.U(1) };
        if (Succeeded(hr)) hr = EffectCcw.Create(pv, ClsidGaussianBlur, "Blur", EffectCcw.KindBlur, bp, 3, backdrop, &blur);
        // LinearTransfer: per channel (offset, slope, disable); alpha disabled (slope 1, offset 0 either way); ClampOutput.
        Prop* lp = stackalloc Prop[]
        {
            Prop.F(0f), Prop.F(1f), Prop.B(false),
            Prop.F(0f), Prop.F(1f), Prop.B(false),
            Prop.F(0f), Prop.F(1f), Prop.B(false),
            Prop.F(0f), Prop.F(1f), Prop.B(true),
            Prop.B(true),
        };
        if (Succeeded(hr)) hr = EffectCcw.Create(pv, ClsidLinearTransfer, "Lum", EffectCcw.KindLinearTransfer, lp, 13, (IGraphicsEffectSource*)blur, &lum);
        if (Succeeded(hr)) hr = StringListCcw.Create([BlurAmount, LumSlopes[0], LumSlopes[1], LumSlopes[2], LumOffsets[0], LumOffsets[1], LumOffsets[2]], &names);
        if (Succeeded(hr)) hr = comp->CreateEffectFactoryWithProperties((IGraphicsEffect*)lum, (IIterable<HSTRING>*)names, factory);
        if (Succeeded(hr) && !Succeeded((*factory)->get_LoadStatus(status)))
            *status = CompositionEffectFactoryLoadStatus.CompositionEffectFactoryLoadStatus_Other;
        if (names != null) StringListCcw.ReleaseRef(names);
        if (lum != null) EffectCcw.ReleaseRef(lum);
        if (blur != null) EffectCcw.ReleaseRef(blur);
        if (backdrop != null) backdrop->Release();
        if (sp != null) sp->Release();
        if (spf != null) spf->Release();
        if (pv != null) pv->Release();
        return hr;
    }

    private static HRESULT Activation(string cls, Guid* iid, void** factory)
    {
        fixed (char* p = cls)
        {
            HSTRING_HEADER h;
            HSTRING s;
            HRESULT hr = WindowsCreateStringReference(p, (uint)cls.Length, &h, &s);
            return Succeeded(hr) ? RoGetActivationFactory(s, iid, factory) : hr;
        }
    }

    private static bool Succeeded(HRESULT hr) => (int)hr >= 0;

    private const int S_OK = 0, E_POINTER = unchecked((int)0x80004003), E_NOINTERFACE = unchecked((int)0x80004002),
        E_INVALIDARG = unchecked((int)0x80070057), E_BOUNDS = unchecked((int)0x8000000B);

    // IInspectable's three methods, shared by every object here: no iids to list, no class name, base trust.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int GetIids(void* self, uint* count, Guid** iids)
    {
        if (count == null || iids == null) return E_POINTER;
        *count = 0; *iids = null;
        return S_OK;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int GetRuntimeClassName(void* self, HSTRING* name)
    {
        if (name == null) return E_POINTER;
        *name = default;
        return S_OK;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int GetTrustLevel(void* self, TrustLevel* level)
    {
        if (level == null) return E_POINTER;
        *level = TrustLevel.BaseTrust;
        return S_OK;
    }

    private static void** InspectableVtbl(int slots, delegate* unmanaged[MemberFunction]<void*, Guid*, void**, int> qi,
        delegate* unmanaged[MemberFunction]<void*, uint> addRef, delegate* unmanaged[MemberFunction]<void*, uint> release)
    {
        void** v = (void**)NativeMemory.Alloc((nuint)slots, (nuint)sizeof(void*));
        v[0] = qi; v[1] = addRef; v[2] = release;
        v[3] = (delegate* unmanaged[MemberFunction]<void*, uint*, Guid**, int>)&GetIids;
        v[4] = (delegate* unmanaged[MemberFunction]<void*, HSTRING*, int>)&GetRuntimeClassName;
        v[5] = (delegate* unmanaged[MemberFunction]<void*, TrustLevel*, int>)&GetTrustLevel;
        return v;
    }

    /// <summary>One property value as the creator states it, boxed into an <c>IPropertyValue</c> at creation.</summary>
    private struct Prop
    {
        public byte Kind;   // 0 Single, 1 UInt32, 2 Boolean
        public float Single;
        public uint UInt;
        public static Prop F(float v) => new() { Kind = 0, Single = v };
        public static Prop U(uint v) => new() { Kind = 1, UInt = v };
        public static Prop B(bool v) => new() { Kind = 2, UInt = v ? 1u : 0u };
    }

    /// <summary>One D2D effect of the graph: <c>IGraphicsEffect</c> (also answered for <c>IGraphicsEffectSource</c>, <c>IInspectable</c>,
    /// <c>IUnknown</c>, <c>IAgileObject</c>) on <see cref="Vtbl"/>, <c>IGraphicsEffectD2D1Interop</c> on <see cref="InteropVtbl"/>.</summary>
    private struct EffectCcw
    {
        public void** Vtbl;
        public void** InteropVtbl;
        public int Rc;
        public int Kind;
        public Guid Clsid;
        public HSTRING Name;                    // owned
        public IGraphicsEffectSource* Source;   // owned
        public uint PropCount;
        public IPropertyValue** Props;          // owned array of owned values

        public const int KindBlur = 0, KindLinearTransfer = 1;

        // The D2D property index of each name (Win2D's names for the same effects; the compositor asks by the part after the dot).
        private static readonly string[] s_blurNames = { "BlurAmount", "Optimization", "BorderMode" };
        private static readonly string[] s_linearNames =
        {
            "RedOffset", "RedSlope", "RedDisable", "GreenOffset", "GreenSlope", "GreenDisable",
            "BlueOffset", "BlueSlope", "BlueDisable", "AlphaOffset", "AlphaSlope", "AlphaDisable", "ClampOutput",
        };

        private static readonly void** s_vtbl = BuildVtbl();
        private static readonly void** s_interopVtbl = BuildInteropVtbl();

        private static void** BuildVtbl()
        {
            void** v = InspectableVtbl(8, &QueryInterface, &AddRef, &Release);
            v[6] = (delegate* unmanaged[MemberFunction]<EffectCcw*, HSTRING*, int>)&GetName;
            v[7] = (delegate* unmanaged[MemberFunction]<EffectCcw*, HSTRING, int>)&PutName;
            return v;
        }

        private static void** BuildInteropVtbl()
        {
            void** v = (void**)NativeMemory.Alloc(9, (nuint)sizeof(void*));
            v[0] = (delegate* unmanaged[MemberFunction]<void**, Guid*, void**, int>)&InteropQueryInterface;
            v[1] = (delegate* unmanaged[MemberFunction]<void**, uint>)&InteropAddRef;
            v[2] = (delegate* unmanaged[MemberFunction]<void**, uint>)&InteropRelease;
            v[3] = (delegate* unmanaged[MemberFunction]<void**, Guid*, int>)&GetEffectId;
            v[4] = (delegate* unmanaged[MemberFunction]<void**, char*, uint*, GRAPHICS_EFFECT_PROPERTY_MAPPING*, int>)&GetNamedPropertyMapping;
            v[5] = (delegate* unmanaged[MemberFunction]<void**, uint*, int>)&GetPropertyCount;
            v[6] = (delegate* unmanaged[MemberFunction]<void**, uint, IPropertyValue**, int>)&GetProperty;
            v[7] = (delegate* unmanaged[MemberFunction]<void**, uint, IGraphicsEffectSource**, int>)&GetSource;
            v[8] = (delegate* unmanaged[MemberFunction]<void**, uint*, int>)&GetSourceCount;
            return v;
        }

        /// <summary>A new effect holding one reference (the caller's) on itself and one on <paramref name="source"/>.</summary>
        public static HRESULT Create(IPropertyValueStatics* pv, Guid clsid, string name, int kind, Prop* props, uint count,
            IGraphicsEffectSource* source, EffectCcw** result)
        {
            *result = null;
            var e = (EffectCcw*)NativeMemory.AllocZeroed((nuint)sizeof(EffectCcw));
            e->Vtbl = s_vtbl; e->InteropVtbl = s_interopVtbl; e->Rc = 1; e->Kind = kind; e->Clsid = clsid;
            e->Props = (IPropertyValue**)NativeMemory.AllocZeroed(count, (nuint)sizeof(void*));
            e->PropCount = count;
            e->Source = source;
            ((IUnknown*)source)->AddRef();
            HRESULT hr;
            fixed (char* p = name) hr = WindowsCreateString(p, (uint)name.Length, &e->Name);
            for (uint i = 0; Succeeded(hr) && i < count; i++)
            {
                IInspectable* boxed = null;
                hr = props[i].Kind switch
                {
                    0 => pv->CreateSingle(props[i].Single, &boxed),
                    1 => pv->CreateUInt32(props[i].UInt, &boxed),
                    _ => pv->CreateBoolean((byte)props[i].UInt, &boxed),
                };
                if (Succeeded(hr))
                {
                    hr = boxed->QueryInterface(__uuidof<IPropertyValue>(), (void**)&e->Props[i]);
                    boxed->Release();
                }
            }
            if (!Succeeded(hr)) { ReleaseRef(e); return hr; }
            *result = e;
            return hr;
        }

        public static uint ReleaseRef(EffectCcw* e)
        {
            int rc = Interlocked.Decrement(ref e->Rc);
            if (rc != 0) return (uint)rc;
            for (uint i = 0; i < e->PropCount; i++)
                if (e->Props[i] != null) e->Props[i]->Release();
            NativeMemory.Free(e->Props);
            if (e->Source != null) ((IUnknown*)e->Source)->Release();
            WindowsDeleteString(e->Name);
            NativeMemory.Free(e);
            return 0;
        }

        private static EffectCcw* FromInterop(void** p) => (EffectCcw*)((byte*)p - sizeof(void*));

        private static int QueryCore(EffectCcw* self, Guid* riid, void** ppv)
        {
            if (ppv == null) return E_POINTER;
            Guid iid = *riid;
            if (iid == *(Guid*)__uuidof<IUnknown>() || iid == *(Guid*)__uuidof<IInspectable>() || iid == *(Guid*)__uuidof<IGraphicsEffect>()
                || iid == *(Guid*)__uuidof<IGraphicsEffectSource>() || iid == *(Guid*)__uuidof<IAgileObject>())
                *ppv = self;
            else if (iid == *(Guid*)__uuidof<IGraphicsEffectD2D1Interop>())
                *ppv = &self->InteropVtbl;
            else { *ppv = null; return E_NOINTERFACE; }
            Interlocked.Increment(ref self->Rc);
            return S_OK;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int QueryInterface(void* self, Guid* riid, void** ppv) => QueryCore((EffectCcw*)self, riid, ppv);
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static uint AddRef(void* self) => (uint)Interlocked.Increment(ref ((EffectCcw*)self)->Rc);
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static uint Release(void* self) => ReleaseRef((EffectCcw*)self);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int GetName(EffectCcw* self, HSTRING* name) => name == null ? E_POINTER : (int)WindowsDuplicateString(self->Name, name);
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int PutName(EffectCcw* self, HSTRING name)
        {
            HSTRING copy;
            HRESULT hr = WindowsDuplicateString(name, &copy);
            if (!Succeeded(hr)) return (int)hr;
            WindowsDeleteString(self->Name);
            self->Name = copy;
            return S_OK;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int InteropQueryInterface(void** self, Guid* riid, void** ppv) => QueryCore(FromInterop(self), riid, ppv);
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static uint InteropAddRef(void** self) => (uint)Interlocked.Increment(ref FromInterop(self)->Rc);
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static uint InteropRelease(void** self) => ReleaseRef(FromInterop(self));

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int GetEffectId(void** self, Guid* id)
        {
            if (id == null) return E_POINTER;
            *id = FromInterop(self)->Clsid;
            return S_OK;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int GetNamedPropertyMapping(void** self, char* name, uint* index, GRAPHICS_EFFECT_PROPERTY_MAPPING* mapping)
        {
            if (name == null || index == null || mapping == null) return E_POINTER;
            string[] names = FromInterop(self)->Kind == KindBlur ? s_blurNames : s_linearNames;
            ReadOnlySpan<char> want = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(name);
            for (int i = 0; i < names.Length; i++)
            {
                if (!want.Equals(names[i], StringComparison.OrdinalIgnoreCase)) continue;
                *index = (uint)i;
                *mapping = GRAPHICS_EFFECT_PROPERTY_MAPPING.GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT;
                return S_OK;
            }
            return E_INVALIDARG;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int GetPropertyCount(void** self, uint* count)
        {
            if (count == null) return E_POINTER;
            *count = FromInterop(self)->PropCount;
            return S_OK;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int GetProperty(void** self, uint index, IPropertyValue** value)
        {
            if (value == null) return E_POINTER;
            EffectCcw* e = FromInterop(self);
            if (index >= e->PropCount) { *value = null; return E_INVALIDARG; }
            *value = e->Props[index];
            e->Props[index]->AddRef();
            return S_OK;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int GetSource(void** self, uint index, IGraphicsEffectSource** source)
        {
            if (source == null) return E_POINTER;
            EffectCcw* e = FromInterop(self);
            if (index != 0) { *source = null; return E_INVALIDARG; }
            *source = e->Source;
            ((IUnknown*)e->Source)->AddRef();
            return S_OK;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int GetSourceCount(void** self, uint* count)
        {
            if (count == null) return E_POINTER;
            *count = 1;
            return S_OK;
        }
    }

    // IIterable<String> and IIterator<String> (parameterized IIDs, windows.foundation.collections.h).
    private static readonly Guid IidIterableString = new(0xe2fcc7c1, 0x3bfc, 0x5a0b, 0xb2, 0xb0, 0x72, 0xe7, 0x69, 0xd1, 0xcb, 0x7e);
    private static readonly Guid IidIteratorString = new(0x8c304ebb, 0x6615, 0x50a4, 0x88, 0x29, 0x87, 0x9e, 0xcd, 0x44, 0x32, 0x36);

    /// <summary>The animatable property names as an <c>IIterable&lt;String&gt;</c>.</summary>
    private struct StringListCcw
    {
        public void** Vtbl;
        public int Rc;
        public uint Count;
        public HSTRING* Items;   // owned

        private static readonly void** s_vtbl = BuildVtbl();

        private static void** BuildVtbl()
        {
            void** v = InspectableVtbl(7, &QueryInterface, &AddRef, &Release);
            v[6] = (delegate* unmanaged[MemberFunction]<StringListCcw*, StringIteratorCcw**, int>)&First;
            return v;
        }

        public static HRESULT Create(ReadOnlySpan<string> items, StringListCcw** result)
        {
            var l = (StringListCcw*)NativeMemory.AllocZeroed((nuint)sizeof(StringListCcw));
            l->Vtbl = s_vtbl; l->Rc = 1;
            l->Items = (HSTRING*)NativeMemory.AllocZeroed((nuint)items.Length, (nuint)sizeof(HSTRING));
            l->Count = (uint)items.Length;
            HRESULT hr = S_OK;
            for (int i = 0; Succeeded(hr) && i < items.Length; i++)
                fixed (char* p = items[i]) hr = WindowsCreateString(p, (uint)items[i].Length, &l->Items[i]);
            if (!Succeeded(hr)) { ReleaseRef(l); l = null; }
            *result = l;
            return hr;
        }

        public static uint ReleaseRef(StringListCcw* l)
        {
            int rc = Interlocked.Decrement(ref l->Rc);
            if (rc != 0) return (uint)rc;
            for (uint i = 0; i < l->Count; i++) WindowsDeleteString(l->Items[i]);
            NativeMemory.Free(l->Items);
            NativeMemory.Free(l);
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int QueryInterface(void* self, Guid* riid, void** ppv)
        {
            if (ppv == null) return E_POINTER;
            Guid iid = *riid;
            if (iid != *(Guid*)__uuidof<IUnknown>() && iid != *(Guid*)__uuidof<IInspectable>() && iid != IidIterableString && iid != *(Guid*)__uuidof<IAgileObject>())
            { *ppv = null; return E_NOINTERFACE; }
            *ppv = self;
            Interlocked.Increment(ref ((StringListCcw*)self)->Rc);
            return S_OK;
        }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static uint AddRef(void* self) => (uint)Interlocked.Increment(ref ((StringListCcw*)self)->Rc);
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static uint Release(void* self) => ReleaseRef((StringListCcw*)self);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int First(StringListCcw* self, StringIteratorCcw** first)
        {
            if (first == null) return E_POINTER;
            *first = StringIteratorCcw.Create(self);
            return S_OK;
        }
    }

    private struct StringIteratorCcw
    {
        public void** Vtbl;
        public int Rc;
        public uint Pos;
        public StringListCcw* List;   // one reference

        private static readonly void** s_vtbl = BuildVtbl();

        private static void** BuildVtbl()
        {
            void** v = InspectableVtbl(10, &QueryInterface, &AddRef, &Release);
            v[6] = (delegate* unmanaged[MemberFunction]<StringIteratorCcw*, HSTRING*, int>)&GetCurrent;
            v[7] = (delegate* unmanaged[MemberFunction]<StringIteratorCcw*, byte*, int>)&GetHasCurrent;
            v[8] = (delegate* unmanaged[MemberFunction]<StringIteratorCcw*, byte*, int>)&MoveNext;
            v[9] = (delegate* unmanaged[MemberFunction]<StringIteratorCcw*, uint, HSTRING*, uint*, int>)&GetMany;
            return v;
        }

        public static StringIteratorCcw* Create(StringListCcw* list)
        {
            var it = (StringIteratorCcw*)NativeMemory.AllocZeroed((nuint)sizeof(StringIteratorCcw));
            it->Vtbl = s_vtbl; it->Rc = 1; it->List = list;
            Interlocked.Increment(ref list->Rc);
            return it;
        }

        private static uint ReleaseRef(StringIteratorCcw* it)
        {
            int rc = Interlocked.Decrement(ref it->Rc);
            if (rc != 0) return (uint)rc;
            StringListCcw.ReleaseRef(it->List);
            NativeMemory.Free(it);
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int QueryInterface(void* self, Guid* riid, void** ppv)
        {
            if (ppv == null) return E_POINTER;
            Guid iid = *riid;
            if (iid != *(Guid*)__uuidof<IUnknown>() && iid != *(Guid*)__uuidof<IInspectable>() && iid != IidIteratorString && iid != *(Guid*)__uuidof<IAgileObject>())
            { *ppv = null; return E_NOINTERFACE; }
            *ppv = self;
            Interlocked.Increment(ref ((StringIteratorCcw*)self)->Rc);
            return S_OK;
        }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static uint AddRef(void* self) => (uint)Interlocked.Increment(ref ((StringIteratorCcw*)self)->Rc);
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static uint Release(void* self) => ReleaseRef((StringIteratorCcw*)self);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int GetCurrent(StringIteratorCcw* self, HSTRING* current)
        {
            if (current == null) return E_POINTER;
            if (self->Pos >= self->List->Count) { *current = default; return E_BOUNDS; }
            return (int)WindowsDuplicateString(self->List->Items[self->Pos], current);
        }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int GetHasCurrent(StringIteratorCcw* self, byte* has)
        {
            if (has == null) return E_POINTER;
            *has = self->Pos < self->List->Count ? (byte)1 : (byte)0;
            return S_OK;
        }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int MoveNext(StringIteratorCcw* self, byte* has)
        {
            if (has == null) return E_POINTER;
            if (self->Pos < self->List->Count) self->Pos++;
            *has = self->Pos < self->List->Count ? (byte)1 : (byte)0;
            return S_OK;
        }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int GetMany(StringIteratorCcw* self, uint capacity, HSTRING* items, uint* actual)
        {
            if (actual == null || (items == null && capacity != 0)) return E_POINTER;
            uint n = 0;
            while (n < capacity && self->Pos < self->List->Count)
            {
                HRESULT hr = WindowsDuplicateString(self->List->Items[self->Pos], &items[n]);
                if (!Succeeded(hr))
                {
                    for (uint i = 0; i < n; i++) WindowsDeleteString(items[i]);
                    *actual = 0;
                    return (int)hr;
                }
                self->Pos++;
                n++;
            }
            *actual = n;
            return S_OK;
        }
    }
}
