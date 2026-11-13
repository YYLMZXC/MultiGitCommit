using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MultiGitCommit;

/// <summary>
/// settings.json 里凭据字段（账号与令牌）的加解密。
///
/// 内存中永远是明文（UI 编辑、git 注入 URL 都直接取用），只有落盘那一刻才加密：
///   * Windows：DPAPI（CryptProtectData，绑定当前 Windows 用户），密钥由系统保管，不落盘；
///   * 其他平台：AES-256-CBC + HMAC-SHA256（Encrypt-then-MAC），密钥放在数据目录的
///     credential.key（随机 32 字节，Unix 权限 600；移动端依赖应用沙盒隔离）。
/// 密文统一带 enc: 前缀，解密时按前缀分派；没有前缀的旧版明文配置原样读取，
/// 下一次保存时自动升级为密文，无需用户迁移。
///
/// 注意：密文与 Windows 用户 / 本机密钥文件绑定，配置文件跨机器、跨用户、跨平台复制后
/// 凭据会解不开（设置页会提示重新填写），其余字段不受影响。
/// </summary>
internal static class CredentialProtector
{
    private const string LegacyPrefix = "enc:";
    private const string WindowsPrefix = "enc:w1:";
    private const string AesPrefix = "enc:a1:";
    private const int KeyLength = 32;
    private const int IvLength = 16;
    private const int MacLength = 32;
    private const uint CryptProtectUiForbidden = 0x1;

    private static readonly object s_keyLock = new();
    private static int s_unprotectFailures;

    /// <summary>本次进程内解密失败的次数（密钥丢失、密文损坏、换了用户/机器等）。</summary>
    public static int UnprotectFailureCount => Volatile.Read(ref s_unprotectFailures);

    /// <summary>把账号 / 令牌明文转成可落盘的密文；空值与已经是密文的值原样返回。</summary>
    public static string Protect(string plain, string keyFilePath)
    {
        if (string.IsNullOrEmpty(plain) || plain.StartsWith(LegacyPrefix, StringComparison.Ordinal))
            return plain;

        byte[] data = Encoding.UTF8.GetBytes(plain);
        byte[] protectedBytes = OperatingSystem.IsWindows()
            ? ProtectWithDpapi(data)
            : ProtectWithAes(data, keyFilePath);

        return (OperatingSystem.IsWindows() ? WindowsPrefix : AesPrefix) +
               Convert.ToBase64String(protectedBytes);
    }

    /// <summary>
    /// 把落盘值还原为内存明文。旧配置（无 enc: 前缀）直接返回；
    /// 解密失败时返回空串并计入失败次数，绝不把密文当作密码交给 git。
    /// </summary>
    public static string Unprotect(string value, string keyFilePath)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(LegacyPrefix, StringComparison.Ordinal))
            return value;

        try
        {
            if (value.StartsWith(WindowsPrefix, StringComparison.Ordinal))
                return Encoding.UTF8.GetString(UnprotectWithDpapi(Convert.FromBase64String(value.Substring(WindowsPrefix.Length))));

            if (value.StartsWith(AesPrefix, StringComparison.Ordinal))
                return Encoding.UTF8.GetString(UnprotectWithAes(Convert.FromBase64String(value.Substring(AesPrefix.Length)), keyFilePath));

            throw new FormatException("未知的凭据密文格式");
        }
        catch (Exception)
        {
            Interlocked.Increment(ref s_unprotectFailures);
            return string.Empty;
        }
    }

    // ==================== 非 Windows：AES-256-CBC + HMAC-SHA256 ====================

    private static byte[] ProtectWithAes(byte[] data, string keyFilePath)
    {
        byte[] key = GetOrCreateKey(keyFilePath);
        byte[] iv = RandomNumberGenerator.GetBytes(IvLength);

        byte[] cipher;
        using (var aes = Aes.Create())
        {
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = DeriveKey(key, "enc");
            aes.IV = iv;
            using ICryptoTransform encryptor = aes.CreateEncryptor();
            cipher = encryptor.TransformFinalBlock(data, 0, data.Length);
        }

        // 载荷 = 版本(1) + IV(16) + 密文，整体再做 HMAC（Encrypt-then-MAC）
        byte[] body = new byte[1 + IvLength + cipher.Length];
        body[0] = 1;
        Buffer.BlockCopy(iv, 0, body, 1, IvLength);
        Buffer.BlockCopy(cipher, 0, body, 1 + IvLength, cipher.Length);

        byte[] mac;
        using (var hmac = new HMACSHA256(DeriveKey(key, "mac")))
            mac = hmac.ComputeHash(body);

        byte[] blob = new byte[body.Length + MacLength];
        Buffer.BlockCopy(body, 0, blob, 0, body.Length);
        Buffer.BlockCopy(mac, 0, blob, body.Length, MacLength);
        return blob;
    }

    private static byte[] UnprotectWithAes(byte[] blob, string keyFilePath)
    {
        if (blob.Length <= 1 + IvLength + MacLength || blob[0] != 1)
            throw new FormatException("凭据密文长度或版本不正确");

        int bodyLength = blob.Length - MacLength;
        byte[] key = ReadKey(keyFilePath) ?? throw new FileNotFoundException("凭据密钥文件不存在");

        byte[] expectedMac;
        using (var hmac = new HMACSHA256(DeriveKey(key, "mac")))
            expectedMac = hmac.ComputeHash(blob, 0, bodyLength);

        if (!CryptographicOperations.FixedTimeEquals(expectedMac, blob.AsSpan(bodyLength, MacLength)))
            throw new CryptographicException("凭据密文校验失败");

        byte[] iv = blob.AsSpan(1, IvLength).ToArray();
        byte[] cipher = blob.AsSpan(1 + IvLength, bodyLength - 1 - IvLength).ToArray();
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = DeriveKey(key, "enc");
        aes.IV = iv;
        using ICryptoTransform decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
    }

    /// <summary>从主密钥派生两个互不混用的子密钥（加密 / 认证）。</summary>
    private static byte[] DeriveKey(byte[] key, string label) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("MultiGitCommit:" + label));

    /// <summary>读取密钥文件；不存在或损坏返回 null（读取路径不新建，避免覆盖原有密钥）。</summary>
    private static byte[]? ReadKey(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            byte[] key = File.ReadAllBytes(path);
            return key.Length == KeyLength ? key : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>首次保存时生成随机密钥，收紧为仅当前用户可读写。</summary>
    private static byte[] GetOrCreateKey(string path)
    {
        lock (s_keyLock)
        {
            byte[]? existing = ReadKey(path);
            if (existing != null) return existing;

            byte[] key = RandomNumberGenerator.GetBytes(KeyLength);
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, key);
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch (Exception)
                {
                    // 文件系统不支持权限位时忽略
                }
            }
            return key;
        }
    }

    // ==================== Windows：DPAPI（当前用户） ====================
    // 不用 System.Security.Cryptography.ProtectedData NuGet 包：本项目是单项目多目标，
    // 直接 P/Invoke 系统 crypt32.dll 可以让 Android / iOS 目标零新增依赖、零条件编译。

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptoBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref CryptoBlob dataIn, string? description,
        IntPtr optionalEntropy, IntPtr reserved, IntPtr promptStruct, uint flags, out CryptoBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref CryptoBlob dataIn, IntPtr description,
        IntPtr optionalEntropy, IntPtr reserved, IntPtr promptStruct, uint flags, out CryptoBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr handle);

    private static byte[] ProtectWithDpapi(byte[] data)
    {
        IntPtr input = Marshal.AllocHGlobal(data.Length);
        CryptoBlob output = default;
        try
        {
            Marshal.Copy(data, 0, input, data.Length);
            var blobIn = new CryptoBlob { cbData = data.Length, pbData = input };
            if (!CryptProtectData(ref blobIn, "MultiGitCommit credential", IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CryptProtectUiForbidden, out output))
                throw new CryptographicException("CryptProtectData 失败：" + Marshal.GetLastWin32Error());

            byte[] result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input);
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }

    private static byte[] UnprotectWithDpapi(byte[] data)
    {
        IntPtr input = Marshal.AllocHGlobal(data.Length);
        CryptoBlob output = default;
        try
        {
            Marshal.Copy(data, 0, input, data.Length);
            var blobIn = new CryptoBlob { cbData = data.Length, pbData = input };
            if (!CryptUnprotectData(ref blobIn, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CryptProtectUiForbidden, out output))
                throw new CryptographicException("CryptUnprotectData 失败：" + Marshal.GetLastWin32Error());

            byte[] result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input);
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }
}
