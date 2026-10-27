using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using DusmpLauncher.Core;

namespace DusmpLauncher.Authentication;

/// <summary>Small encrypted token vault. The key file is private to the current OS user.</summary>
public sealed class SecureTokenStore : ISecureTokenStore
{
    private readonly string _dataPath, _keyPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public SecureTokenStore(LauncherPaths paths) { _dataPath = Path.Combine(paths.Root, "secrets.bin"); _keyPath = Path.Combine(paths.Root, "secrets.key"); }
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct); try { return (await ReadAsync(ct)).GetValueOrDefault(key); } finally { _gate.Release(); }
    }
    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct); try { var values = await ReadAsync(ct); values[key] = value; await WriteAsync(values, ct); } finally { _gate.Release(); }
    }
    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct); try { var values = await ReadAsync(ct); if (values.Remove(key)) await WriteAsync(values, ct); } finally { _gate.Release(); }
    }
    private async Task<Dictionary<string,string>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_dataPath)) return new(StringComparer.Ordinal);
        var all = await File.ReadAllBytesAsync(_dataPath, ct);
        if (OperatingSystem.IsWindows() && !File.Exists(_keyPath))
        {
            try { return JsonSerializer.Deserialize<Dictionary<string,string>>(Dpapi.Unprotect(all)) ?? new(StringComparer.Ordinal); }
            catch (CryptographicException) { return new(StringComparer.Ordinal); }
        }
        if (!File.Exists(_keyPath) || all.Length < 28) return new(StringComparer.Ordinal);
        var key = await File.ReadAllBytesAsync(_keyPath, ct); var clear = new byte[all.Length - 28];
        try { using var aes = new AesGcm(key, 16); aes.Decrypt(all.AsSpan(0,12), all.AsSpan(28), all.AsSpan(12,16), clear); }
        catch (CryptographicException) { return new(StringComparer.Ordinal); }
        return JsonSerializer.Deserialize<Dictionary<string,string>>(clear) ?? new(StringComparer.Ordinal);
    }
    private async Task WriteAsync(Dictionary<string,string> values, CancellationToken ct)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(values);
        if (OperatingSystem.IsWindows())
        {
            var protectedData = Dpapi.Protect(plain);
            var tempWindows = _dataPath + ".tmp"; await File.WriteAllBytesAsync(tempWindows, protectedData, ct); File.Move(tempWindows, _dataPath, true);
            if (File.Exists(_keyPath)) File.Delete(_keyPath);
            CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(protectedData); return;
        }
        if (!File.Exists(_keyPath))
        {
            var key = RandomNumberGenerator.GetBytes(32); await File.WriteAllBytesAsync(_keyPath, key, ct);
            File.SetUnixFileMode(_keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(await File.ReadAllBytesAsync(_keyPath, ct), 16)) aes.Encrypt(nonce, plain, cipher, tag);
        var output = new byte[nonce.Length + tag.Length + cipher.Length]; nonce.CopyTo(output, 0); tag.CopyTo(output, 12); cipher.CopyTo(output, 28);
        var tmp = _dataPath + ".tmp"; await File.WriteAllBytesAsync(tmp, output, ct); File.Move(tmp, _dataPath, true);
        File.SetUnixFileMode(_dataPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        CryptographicOperations.ZeroMemory(plain);
    }

    private static class Dpapi
    {
        private const uint UiForbidden = 0x1;
        public static byte[] Protect(byte[] bytes) => Transform(bytes, protect: true);
        public static byte[] Unprotect(byte[] bytes) => Transform(bytes, protect: false);
        private static byte[] Transform(byte[] bytes, bool protect)
        {
            var input = new Blob(bytes); DataBlob output;
            try
            {
                var ok = protect
                    ? CryptProtectData(ref input.Value, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                    : CryptUnprotectData(ref input.Value, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
                if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
                try { var result = new byte[output.cbData]; Marshal.Copy(output.pbData, result, 0, result.Length); return result; }
                finally { LocalFree(output.pbData); }
            }
            finally { input.Dispose(); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int cbData; public IntPtr pbData; }
        private sealed class Blob : IDisposable
        {
            public DataBlob Value;
            public Blob(byte[] bytes) { Value.cbData=bytes.Length; Value.pbData=Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes,0,Value.pbData,bytes.Length); }
            public void Dispose() { if (Value.pbData != IntPtr.Zero) { Marshal.FreeHGlobal(Value.pbData); Value.pbData=IntPtr.Zero; } }
        }
        [DllImport("crypt32.dll", SetLastError=true, CharSet=CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
        [DllImport("crypt32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    }
}
