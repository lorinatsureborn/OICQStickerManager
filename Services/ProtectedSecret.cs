using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace OICQStickerManager.Services;

internal static class ProtectedSecret
{
    private const string Prefix = "dpapi:v1:";
    internal static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value) || value.StartsWith(Prefix, StringComparison.Ordinal)) return value;
        var plain = Encoding.UTF8.GetBytes(value);
        try { return Prefix + Convert.ToBase64String(ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    internal static string Unprotect(string value)
    {
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return value;
        var plain = ProtectedData.Unprotect(Convert.FromBase64String(value[Prefix.Length..]), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    internal static string ProtectConfigKeys(string json)
    {
        var node = JsonNode.Parse(json);
        if (node is JsonObject obj && obj["QqDbKey"] is JsonValue key && key.TryGetValue<string>(out var value))
            obj["QqDbKey"] = Protect(value);
        return node?.ToJsonString() ?? json;
    }
}
