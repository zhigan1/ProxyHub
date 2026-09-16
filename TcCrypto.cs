using System.Security.Cryptography;
using System.Text;

namespace ProxyHub;

/// <summary>
/// Trae "tc" 加密值解密：AES-128-CBC + SHA-512 完整性校验。
/// 算法逆向自 Trae 桌面端前端 JS（致谢 laojichao/trae-local-api），盐值原样保留，勿改。
/// 对应上游 traecn.js 的 decryptTc / xorSalts。
/// </summary>
public static class TcCrypto
{
    internal static readonly byte[] SaltA =
    {
        82, 9, 106, 213, 48, 54, 165, 56, 191, 64, 163, 158, 129, 243, 215, 251,
        124, 227, 57, 130, 155, 47, 255, 135, 52, 142, 67, 68, 196, 222, 233, 203,
        84, 123, 148, 50, 166, 194, 35, 61, 238, 76, 149, 11, 66, 250, 195, 78,
        8, 46, 161, 102, 40, 217, 36, 178, 118, 91, 162, 73, 109, 139, 209, 37,
    };

    internal static readonly byte[] SaltB =
    {
        31, 221, 168, 51, 136, 7, 199, 49, 177, 18, 16, 89, 39, 128, 236, 95,
        96, 81, 127, 169, 25, 181, 74, 13, 45, 229, 122, 159, 147, 201, 156, 239,
        160, 224, 59, 77, 174, 42, 245, 176, 200, 235, 187, 60, 131, 83, 153, 97,
        23, 43, 4, 126, 186, 119, 214, 38, 225, 105, 20, 99, 85, 33, 12, 125,
    };

    internal static readonly byte[] SaltC =
    {
        191, 192, 216, 250, 122, 246, 220, 97, 31, 254, 98, 27, 8, 72, 71, 176,
        135, 99, 96, 18, 127, 101, 203, 104, 211, 102, 191, 125, 37, 72, 150, 156,
        51, 229, 121, 35, 17, 153, 141, 177, 110, 131, 150, 128, 172, 255, 254, 6,
        18, 140, 55, 62, 236, 249, 135, 64, 135, 12, 117, 4, 89, 149, 168, 209,
    };

    internal static readonly byte[] SaltD =
    {
        246, 204, 26, 232, 232, 70, 129, 109, 223, 146, 169, 242, 23, 241, 105, 145,
        50, 196, 165, 42, 254, 120, 3, 54, 244, 207, 209, 85, 53, 6, 138, 106,
        175, 148, 31, 204, 186, 186, 165, 182, 87, 142, 49, 10, 39, 110, 26, 154,
        86, 56, 173, 125, 18, 64, 198, 225, 99, 99, 83, 82, 191, 134, 76, 170,
    };

    /// <summary>解密单个 tc 值；完整性校验失败抛异常。</summary>
    public static string DecryptTc(string base64Value)
    {
        var buf = Convert.FromBase64String(base64Value);
        // 结构：[6B header][32B random][密文...]
        var header = buf.AsSpan(0, 6);
        var randomBytes = buf.AsSpan(6, 32);
        var encrypted = buf.AsSpan(38);

        var isPrivate = header[0] == 18 && header[1] == 57; // AES_PRIVATE
        var salt = isPrivate ? XorSalts(SaltC, SaltD) : XorSalts(SaltA, SaltB);

        var h1 = SHA512.HashData(randomBytes);
        var h2 = SHA512.HashData(h1.Concat(salt).ToArray());
        var key = h2[..16];
        var iv = h2[16..32];

        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor(key, iv);
        var decrypted = decryptor.TransformFinalBlock(encrypted.ToArray(), 0, encrypted.Length);

        var storedHash = decrypted.AsSpan(0, 64);
        var plaintext = decrypted.AsSpan(64);
        if (!SHA512.HashData(plaintext).AsSpan().SequenceEqual(storedHash))
            throw new InvalidDataException("Trae tc 完整性校验失败");
        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] XorSalts(byte[] a, byte[] b)
    {
        var output = new byte[a.Length];
        for (var i = 0; i < a.Length; i++) output[i] = (byte)(a[i] ^ b[i]);
        return output;
    }
}
