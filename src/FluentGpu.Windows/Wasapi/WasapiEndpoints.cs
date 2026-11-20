using System;
using System.Collections.Generic;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Windows.Wasapi;

/// <summary>How a render endpoint reaches the machine, read from the OS facts of its device (the enumerator that created
/// it, its container, its form factor and jack). A fact, not a label: an app words it.</summary>
public enum AudioEndpointBus : byte
{
    /// <summary>Nothing the OS reported says how it is connected.</summary>
    Unknown,
    /// <summary>Part of this computer: the endpoint's container is the local machine's own (built-in speakers, the headphone jack).</summary>
    BuiltIn,
    /// <summary>A Bluetooth device (its endpoint enumerated by the Bluetooth stack: <c>BTHENUM</c>, <c>BTHHFENUM</c>, <c>BTHLEDEVICE</c>).</summary>
    Bluetooth,
    /// <summary>A USB audio device (enumerated by <c>USB</c>).</summary>
    Usb,
    /// <summary>A display's audio over HDMI (its jack says so).</summary>
    Hdmi,
    /// <summary>A display's audio over DisplayPort (its jack says so).</summary>
    DisplayPort,
    /// <summary>A display's audio whose link the jack does not name (the digital-display form factor alone).</summary>
    Display,
}

/// <summary>OS-reported render endpoint identity. FormFactor is the Windows EndpointFormFactor value
/// (1 speakers, 3 headphones, 5 headset, 8 SPDIF, 9 digital display, uint.MaxValue unknown).
/// IsDefault describes the default at enumeration/open time; re-enumerate after a device-change notification.
/// <see cref="Name"/> is Windows' endpoint name (<c>PKEY_Device_FriendlyName</c>, "Headphones (Chris's AirPods Max)"); the parts it is
/// made of and the facts of the device behind it are the init members, empty when the OS did not report them.</summary>
public sealed record WasapiEndpointInfo(string Id, string Name, uint FormFactor, bool IsDefault)
{
    /// <summary>The device's own name (<c>PKEY_DeviceInterface_FriendlyName</c>): "Chris's AirPods Max", "USB-C Audio", or the audio
    /// adapter's name for a built-in endpoint ("Realtek(R) Audio").</summary>
    public string DeviceName { get; init; } = "";

    /// <summary>The endpoint's description (<c>PKEY_Device_DeviceDesc</c>): "Speakers", "Headphones", a display's model name.</summary>
    public string Description { get; init; } = "";

    /// <summary>The enumerator that created the device (<c>PKEY_Device_EnumeratorName</c>): "BTHENUM", "USB", "HDAUDIO", "AUCD".</summary>
    public string Enumerator { get; init; } = "";

    /// <summary>The device's container (<c>PKEY_Device_ContainerId</c>): every endpoint of one physical device shares it (a Bluetooth headset's
    /// stereo and hands-free endpoints); <see cref="LocalMachineContainer"/> for a part of this computer; empty when not reported.</summary>
    public Guid ContainerId { get; init; }

    /// <summary>The jack's KS node type (<c>PKEY_AudioEndpoint_JackSubType</c>) as Windows stores it ("{D1B9CC2A-…}" for HDMI), or empty.</summary>
    public string JackSubType { get; init; } = "";

    /// <summary>How the endpoint reaches the machine, decided from the facts above (the enumerator, the jack, the form factor, the container).</summary>
    public AudioEndpointBus Bus { get; init; }

    /// <summary>The container every device that is part of this computer reports ({00000000-0000-0000-FFFF-FFFFFFFFFFFF}).</summary>
    public static readonly Guid LocalMachineContainer = new(0x00000000, 0x0000, 0x0000, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);
}

internal static unsafe class WasapiEndpoints
{
    // Windows SDK property keys read from an endpoint's property store (functiondiscoverykeys_devpkey.h, mmdeviceapi.h).
    static readonly Guid DeviceFmtid = new("a45c254e-df1c-4efd-8020-67d146a850e0");        // PKEY_Device_FriendlyName, _DeviceDesc, _EnumeratorName
    static readonly Guid InterfaceFmtid = new("026e516e-b814-414b-83cd-856d6fef4822");     // PKEY_DeviceInterface_FriendlyName
    static readonly Guid ContainerFmtid = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c");     // PKEY_Device_ContainerId
    static readonly Guid EndpointFmtid = new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e");      // PKEY_AudioEndpoint_FormFactor, _JackSubType
    const uint FriendlyNamePid = 14, DeviceDescPid = 2, EnumeratorPid = 24, InterfaceNamePid = 2, ContainerPid = 2, FormFactorPid = 0, JackPid = 8;

    // KSNODETYPE_HDMI_INTERFACE / KSNODETYPE_DISPLAYPORT_INTERFACE (ksmedia.h): the jack of a display's audio.
    const string HdmiJack = "{D1B9CC2A-F519-417F-91C9-55FA65481001}";
    const string DisplayPortJack = "{E47E4031-3EA6-418D-8F9B-B73843CCBA97}";
    const uint DigitalDisplayFormFactor = 9;

    /// <summary>The device's endpoint id, or null.</summary>
    internal static string? IdOf(IMMDevice* device)
    {
        char* id = null;
        if (device->GetId(&id) < 0 || id == null) return null;
        try { return new string(id); }
        finally { CoTaskMemFree(id); }
    }

    internal static WasapiEndpointInfo? Describe(IMMDevice* device, bool isDefault)
    {
        if (IdOf(device) is not { } endpointId) return null;
        string name = endpointId, deviceName = "", description = "", enumerator = "", jack = "";
        uint formFactor = uint.MaxValue;
        Guid container = default;
        IPropertyStore* store = null;
        if (device->OpenPropertyStore(0, &store) >= 0 && store != null)
        {
            try
            {
                if (Text(store, DeviceFmtid, FriendlyNamePid) is { Length: > 0 } friendly) name = friendly;
                deviceName = Text(store, InterfaceFmtid, InterfaceNamePid);
                description = Text(store, DeviceFmtid, DeviceDescPid);
                enumerator = Text(store, DeviceFmtid, EnumeratorPid);
                jack = Text(store, EndpointFmtid, JackPid);
                container = Clsid(store, ContainerFmtid, ContainerPid);
                formFactor = UInt(store, EndpointFmtid, FormFactorPid);
            }
            finally { store->Release(); }
        }
        return new WasapiEndpointInfo(endpointId, name, formFactor, isDefault)
        {
            DeviceName = deviceName, Description = description, Enumerator = enumerator, ContainerId = container, JackSubType = jack,
            Bus = BusOf(enumerator, container, formFactor, jack),
        };
    }

    /// <summary>How an endpoint reaches the machine, from its OS facts. The enumerator names the stack that created the device
    /// (Bluetooth, USB), a display's jack or form factor names a display link, and the local machine's container marks a part of
    /// this computer. Pure.</summary>
    internal static AudioEndpointBus BusOf(string enumerator, Guid container, uint formFactor, string jack)
    {
        if (enumerator.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) return AudioEndpointBus.Bluetooth;
        if (enumerator.Equals("USB", StringComparison.OrdinalIgnoreCase)) return AudioEndpointBus.Usb;
        if (jack.Equals(HdmiJack, StringComparison.OrdinalIgnoreCase)) return AudioEndpointBus.Hdmi;
        if (jack.Equals(DisplayPortJack, StringComparison.OrdinalIgnoreCase)) return AudioEndpointBus.DisplayPort;
        if (formFactor == DigitalDisplayFormFactor) return AudioEndpointBus.Display;
        if (container == WasapiEndpointInfo.LocalMachineContainer) return AudioEndpointBus.BuiltIn;
        return AudioEndpointBus.Unknown;
    }

    /// <summary>A VT_LPWSTR property, or "" when absent or of another type.</summary>
    static string Text(IPropertyStore* store, Guid fmtid, uint pid)
    {
        PROPERTYKEY key = new() { fmtid = fmtid, pid = pid };
        PROPVARIANT value = default;
        try
        {
            return store->GetValue(&key, &value) >= 0 && (int)value.vt == 31 && value.pwszVal != null ? new string(value.pwszVal) : "";
        }
        finally { PropVariantClear(&value); }
    }

    /// <summary>A VT_UI4 property, or uint.MaxValue.</summary>
    static uint UInt(IPropertyStore* store, Guid fmtid, uint pid)
    {
        PROPERTYKEY key = new() { fmtid = fmtid, pid = pid };
        PROPVARIANT value = default;
        try { return store->GetValue(&key, &value) >= 0 && (int)value.vt == 19 ? value.ulVal : uint.MaxValue; }
        finally { PropVariantClear(&value); }
    }

    /// <summary>A VT_CLSID property, or an empty Guid.</summary>
    static Guid Clsid(IPropertyStore* store, Guid fmtid, uint pid)
    {
        PROPERTYKEY key = new() { fmtid = fmtid, pid = pid };
        PROPVARIANT value = default;
        try { return store->GetValue(&key, &value) >= 0 && (int)value.vt == 72 && value.puuid != null ? *value.puuid : default; }
        finally { PropVariantClear(&value); }
    }

    /// <summary>The console default render endpoint's id right now, or null (no audio service, no endpoint). One enumerator, one
    /// default lookup, no property store: what a caller asks before it keeps an open stream on an endpoint it opened as "the default".</summary>
    internal static string? DefaultId()
    {
        int initialized = CoInitializeEx(null, (uint)(COINIT.COINIT_MULTITHREADED | COINIT.COINIT_DISABLE_OLE1DDE));
        IMMDeviceEnumerator* enumerator = null;
        IMMDevice* device = null;
        try
        {
            Guid clsid = CLSID.CLSID_MMDeviceEnumerator, iid = IID.IID_IMMDeviceEnumerator;
            if (CoCreateInstance(&clsid, null, 0x17, &iid, (void**)&enumerator) < 0) return null;
            if (enumerator->GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, &device) < 0 || device == null) return null;
            return IdOf(device);
        }
        finally
        {
            if (device != null) device->Release();
            if (enumerator != null) enumerator->Release();
            if (initialized >= 0) CoUninitialize();
        }
    }

    internal static WasapiEndpointInfo[] Enumerate()
    {
        int initialized = CoInitializeEx(null, (uint)(COINIT.COINIT_MULTITHREADED | COINIT.COINIT_DISABLE_OLE1DDE));
        IMMDeviceEnumerator* enumerator = null;
        IMMDeviceCollection* collection = null;
        IMMDevice* defaultDevice = null;
        try
        {
            Guid clsid = CLSID.CLSID_MMDeviceEnumerator, iid = IID.IID_IMMDeviceEnumerator;
            if (CoCreateInstance(&clsid, null, 0x17, &iid, (void**)&enumerator) < 0) return [];
            string? defaultId = null;
            if (enumerator->GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, &defaultDevice) >= 0 && defaultDevice != null)
                defaultId = Describe(defaultDevice, true)?.Id;
            if (enumerator->EnumAudioEndpoints(EDataFlow.eRender, 1, &collection) < 0 || collection == null) return [];
            uint count;
            if (collection->GetCount(&count) < 0) return [];
            var result = new List<WasapiEndpointInfo>((int)count);
            for (uint i = 0; i < count; i++)
            {
                IMMDevice* device = null;
                if (collection->Item(i, &device) < 0 || device == null) continue;
                try
                {
                    if (Describe(device, false) is { } info)
                        result.Add(info with { IsDefault = string.Equals(info.Id, defaultId, StringComparison.Ordinal) });
                }
                finally { device->Release(); }
            }
            return result.ToArray();
        }
        finally
        {
            if (defaultDevice != null) defaultDevice->Release();
            if (collection != null) collection->Release();
            if (enumerator != null) enumerator->Release();
            if (initialized >= 0) CoUninitialize();
        }
    }
}
