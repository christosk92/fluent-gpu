using System;
using System.Collections.Generic;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Windows.Wasapi;

/// <summary>OS-reported render endpoint identity. FormFactor is the Windows EndpointFormFactor value
/// (1 speakers, 3 headphones, 5 headset, 8 SPDIF, 9 digital display, uint.MaxValue unknown).
/// IsDefault describes the default at enumeration/open time; re-enumerate after a device-change notification.</summary>
public sealed record WasapiEndpointInfo(string Id, string Name, uint FormFactor, bool IsDefault);

internal static unsafe class WasapiEndpoints
{
    internal static WasapiEndpointInfo? Describe(IMMDevice* device, bool isDefault)
    {
        char* id = null;
        if (device->GetId(&id) < 0 || id == null) return null;
        string endpointId;
        try { endpointId = new string(id); }
        finally { CoTaskMemFree(id); }
        string name = endpointId;
        uint formFactor = uint.MaxValue;
        IPropertyStore* store = null;
        if (device->OpenPropertyStore(0, &store) >= 0 && store != null)
        {
            try
            {
                // Windows SDK PKEY_Device_FriendlyName / PKEY_AudioEndpoint_FormFactor.
                PROPERTYKEY nameKey = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
                PROPERTYKEY formKey = new() { fmtid = new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), pid = 0 };
                PROPVARIANT value = default;
                try
                {
                    if (store->GetValue(&nameKey, &value) >= 0 && (int)value.vt == 31 && value.pwszVal != null)
                        name = new string(value.pwszVal);
                }
                finally { PropVariantClear(&value); }
                value = default;
                try
                {
                    if (store->GetValue(&formKey, &value) >= 0 && (int)value.vt == 19) formFactor = value.ulVal;
                }
                finally { PropVariantClear(&value); }
            }
            finally { store->Release(); }
        }
        return new WasapiEndpointInfo(endpointId, name, formFactor, isDefault);
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
