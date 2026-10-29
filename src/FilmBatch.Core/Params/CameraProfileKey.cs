// D2：raw.camera_profile 使用者 key 的完整格式驗證。dcp-<SHA-256 64 小寫 hex>。
namespace FilmBatch.Core.Params;

/// <summary>
/// 使用者相機描述檔 key：<c>dcp-</c>＋原始 DCP 位元組的 SHA-256（64 個 ASCII 小寫十六進位）。
/// <c>auto</c> 是來源選項，不是使用者 key；本驗證只認完整格式，不做任何備援或正規化。
/// </summary>
public static class CameraProfileKey
{
    /// <summary>key 前綴；之後恰好 <see cref="HexLength"/> 個小寫 hex。</summary>
    public const string Prefix = "dcp-";

    /// <summary>SHA-256 十六進位長度。</summary>
    public const int HexLength = 64;

    /// <summary><paramref name="key"/> 恰為 <c>dcp-</c>＋64 個 ASCII 小寫 hex 時為 true。</summary>
    public static bool IsUserKey(string? key)
    {
        if (key is null || key.Length != Prefix.Length + HexLength ||
            !key.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        for (var i = Prefix.Length; i < key.Length; i++)
        {
            var c = key[i];
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        }
        return true;
    }
}
