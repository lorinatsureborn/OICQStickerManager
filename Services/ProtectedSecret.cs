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
        return ProtectApiKey(value);
    }
    // AI 编辑区始终是明文输入，不把看起来像密文的输入当成已加密值。
    internal static string ProtectApiKey(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var plain = Encoding.UTF8.GetBytes(value);
        try { return Prefix + Convert.ToBase64String(ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    internal static bool IsProtected(string? value) => value?.StartsWith(Prefix, StringComparison.Ordinal) == true;
    internal static string UnprotectApiKey(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (!IsProtected(value)) throw new FormatException("AI key must use the protected format.");
        return Unprotect(value);
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
        var changed = false;
        if (node is JsonObject obj)
        {
            if (obj["QqDbKey"] is JsonValue key && key.TryGetValue<string>(out var value))
            {
                var protectedValue = Protect(value);
                if (protectedValue != value) { obj["QqDbKey"] = protectedValue; changed = true; }
            }
            // AI 功能未发布：丢弃旧明文字段，不做迁移，也不把它们带进备份。
            changed |= obj.Remove("AiTagApiKey");
            if (obj["AiKeyProfiles"] is JsonArray profiles)
                foreach (var profile in profiles.OfType<JsonObject>())
                {
                    changed |= profile.Remove("ApiKey");
                    if (profile["ProtectedApiKey"] is JsonValue secret && secret.TryGetValue<string>(out var cipher)
                        && cipher.Length > 0 && !IsProtected(cipher))
                    { profile["ProtectedApiKey"] = ""; changed = true; }
                }
        }
        return changed ? node!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) : json;
    }
}
