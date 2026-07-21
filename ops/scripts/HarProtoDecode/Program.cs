using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Wavee.Protocol.ExtendedMetadata;

static string Format(IMessage msg) =>
    new JsonFormatter(new JsonFormatter.Settings(formatDefaultValues: true)).Format(msg);

static void DecodeAny(Any any, StringBuilder sb, int indent)
{
    var pad = new string(' ', indent);
    sb.AppendLine($"{pad}type_url: {any.TypeUrl}");
    if (any.Value.IsEmpty)
    {
        sb.AppendLine($"{pad}value: <empty>");
        return;
    }
    sb.AppendLine($"{pad}value_bytes: {any.Value.Length}");
    // Print printable strings embedded in the payload
    var bytes = any.Value.ToByteArray();
    var text = Encoding.UTF8.GetString(bytes);
    foreach (Match m in Regex.Matches(text, @"[\x20-\x7e]{4,}"))
        sb.AppendLine($"{pad}  embedded: {m.Value}");
}

static string DecodeResponse(byte[] body)
{
    BatchedExtensionResponse resp;
    try { resp = BatchedExtensionResponse.Parser.ParseFrom(body); }
    catch (Exception ex) { return $"PARSE ERROR: {ex.Message}"; }

    var sb = new StringBuilder();
    sb.AppendLine("=== BatchedExtensionResponse ===");
    sb.AppendLine($"extended_metadata arrays: {resp.ExtendedMetadata.Count}");
    foreach (var array in resp.ExtendedMetadata)
    {
        sb.AppendLine($"--- ExtensionKind: {array.ExtensionKind} ({(int)array.ExtensionKind}) ---");
        if (array.Header is { } h)
            sb.AppendLine($"  array_header: provider_error={h.ProviderErrorStatus}, cache_ttl={h.CacheTtlInSeconds}s, offline_ttl={h.OfflineTtlInSeconds}s, type={h.ExtensionType}");
        foreach (var data in array.ExtensionData)
        {
            sb.AppendLine($"  entity_uri: {data.EntityUri}");
            if (data.Header is { } eh)
                sb.AppendLine($"    status={eh.StatusCode}, etag={eh.Etag}, cache_ttl={eh.CacheTtlInSeconds}s, offline_ttl={eh.OfflineTtlInSeconds}s");
            if (data.ExtensionData is { } any)
                DecodeAny(any, sb, 4);
        }
    }
    return sb.ToString();
}

static string DecodeRequest(byte[] body)
{
    var req = BatchedEntityRequest.Parser.ParseFrom(body);
    var sb = new StringBuilder();
    sb.AppendLine("=== BatchedEntityRequest ===");
    sb.AppendLine(Format(req));
    sb.AppendLine();
    if (req.Header is { } h)
        sb.AppendLine($"header: country={h.Country}, catalogue={h.Catalogue}, task_id={Convert.ToHexString(h.TaskId.ToByteArray())}");
    foreach (var er in req.EntityRequest)
    {
        sb.AppendLine($"entity_uri: {er.EntityUri}");
        foreach (var q in er.Query)
            sb.AppendLine($"  kind: {q.ExtensionKind} ({(int)q.ExtensionKind})" + (q.HasEtag ? $", etag={q.Etag}" : ""));
    }
    return sb.ToString();
}

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: HarProtoDecode <extracted-dir>");
    return 1;
}

var dir = new DirectoryInfo(args[0]);
var manifest = JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(Path.Combine(dir.FullName, "manifest.json")))!;
var summary = new List<object>();

foreach (var item in manifest)
{
    var prefix = Path.GetFileNameWithoutExtension(item.GetProperty("request_file").GetString()!).Replace("_request", "");
    var reqPath = Path.Combine(dir.FullName, item.GetProperty("request_file").GetString()!);
    var respPath = Path.Combine(dir.FullName, item.GetProperty("response_file").GetString()!);
    var feature = item.GetProperty("feature").GetString();

    string reqText, respText;
    try { reqText = DecodeRequest(File.ReadAllBytes(reqPath)); }
    catch (Exception ex) { reqText = $"PARSE ERROR: {ex.Message}"; }
    try { respText = DecodeResponse(File.ReadAllBytes(respPath)); }
    catch (Exception ex) { respText = $"PARSE ERROR: {ex.Message}"; }

    File.WriteAllText(Path.Combine(dir.FullName, $"{prefix}_request.txt"), reqText);
    File.WriteAllText(Path.Combine(dir.FullName, $"{prefix}_response.txt"), respText);

    summary.Add(new { feature, request = $"{prefix}_request.txt", response = $"{prefix}_response.txt" });
    Console.WriteLine($"Decoded [{prefix}] {feature}");
}

File.WriteAllText(Path.Combine(dir.FullName, "decoded_summary.json"),
    JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
return 0;
