using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wavee.Backend.Audio;

namespace Wavee.Tests.Audio;

static class PlayPlayRuntimeTestFixtures
{
    const ushort ImageFileMachineAmd64 = 0x8664;
    const ushort ImageFileMachineArm64 = 0xAA64;

    public static ushort ProcessPeMachine => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => ImageFileMachineArm64,
        _ => ImageFileMachineAmd64,
    };

    public static string ProcessArchLabel => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "Arm64",
        _ => "X64",
    };

    public static byte[] WriteMinimalPe(string path, ushort? machine = null)
    {
        var peMachine = machine ?? ProcessPeMachine;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var dos = new byte[64];
        dos[0] = (byte)'M';
        dos[1] = (byte)'Z';
        const int peOffset = 0x40;
        BitConverter.TryWriteBytes(dos.AsSpan(0x3C, 4), peOffset);
        using (var fs = File.Create(path))
        {
            fs.Write(dos);
            fs.Position = peOffset;
            fs.Write([(byte)'P', (byte)'E', (byte)0, (byte)0]);
            fs.Write(BitConverter.GetBytes(peMachine));
        }
        return SHA256.HashData(File.ReadAllBytes(path));
    }

    /// <summary>An in-memory minimal PE for the given machine, padded to <paramref name="size"/> bytes (so downloader
    /// tests can exercise multi-chunk progress). Valid enough for <c>TryGetPeArchitecture</c> (DOS + PE header only).</summary>
    public static byte[] MinimalPeBytes(int size = 0x46, ushort? machine = null)
    {
        var peMachine = machine ?? ProcessPeMachine;
        var buf = new byte[Math.Max(size, 0x46)];
        buf[0] = (byte)'M';
        buf[1] = (byte)'Z';
        BitConverter.TryWriteBytes(buf.AsSpan(0x3C, 4), 0x40);
        buf[0x40] = (byte)'P';
        buf[0x41] = (byte)'E';
        BitConverter.TryWriteBytes(buf.AsSpan(0x44, 2), peMachine);
        return buf;
    }

    public static PlayPlayRuntimeManifest SampleManifest(string? arch = null, string? dllSha256 = null)
    {
        arch ??= ProcessArchLabel;
        var suffix = arch.Equals("Arm64", StringComparison.OrdinalIgnoreCase) ? "arm64" : "x64";
        return new(
        SchemaVersion: 1,
        PackId: $"129300667-{suffix}",
        SpotifyVersion: "1.2.93.667",
        ClientVersion: "1.2.93.667.g7b5cc0ce",
        AppVersion: "129300667",
        PlayPlayRequestVersion: 5,
        Arch: arch,
        AlgorithmVersion: "129300667-native-cdn-v2",
        DllSha256: dllSha256 ?? new string('a', 64),
        PlayPlayTokenHex: "025614bf92a6c95e922e466523da4f96",
        Config: new PlayPlayRuntimeManifestConfig(
            AnalysisBaseHex: "180000000",
            VmRuntimeInitVaHex: "1804B35F8",
            VmObjectTransformVaHex: "1804B5538",
            RuntimeContextVaHex: "1818C4848",
            RuntimeContextSecondaryVaHex: "1818B3730",
            InitVtableLabsHex: ["1818B4598"],
            TransformFourthArgTemplateVaHex: "182674ED8",
            TransformFourthArgBuildVaHex: "1815EA8CC",
            FillRandomBytesVaHex: "180434B04",
            VmInitValueHex: "00000000000000000000000000000000",
            AesKey: new AesKeyExtraction.OutputBufferSlice(0, 16),
            VmObjectSize: 144,
            RtContextSize: 16,
            DerivedKeySize: 32,
            ObfuscatedKeySize: 16,
            InitValueSize: 16,
            ContentIdSize: 16,
            KeySize: 16));
    }

    public static string WriteRuntimeDir(string root, PlayPlayRuntimeManifest manifest)
    {
        Directory.CreateDirectory(root);
        var dll = Path.Combine(root, "Spotify.dll");
        var hash = WriteMinimalPe(dll);
        var sha = Convert.ToHexStringLower(hash);
        var m = manifest with { DllSha256 = sha, PackId = manifest.PackId, Arch = manifest.Arch };
        var json = JsonSerializer.Serialize(m, PlayPlayManifestJson.Default.PlayPlayRuntimeManifest);
        File.WriteAllText(Path.Combine(root, "playplay-runtime.json"), json);
        return sha;
    }
}
