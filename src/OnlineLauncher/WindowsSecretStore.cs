using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OnlineLauncher;

public sealed class AuthSession
{
    public string Email { get; set; } = "";
    public string UserId { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTimeOffset AccessTokenExpiresAtUtc { get; set; }
    public string? CurrentSessionId { get; set; }
}

public static class WindowsSecretStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static string RootPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "1.61ONLINE", "sessions");

    public static AuthSession? Load(string environmentName)
    {
        var path = GetPath(environmentName);
        if (!File.Exists(path)) return null;

        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var plainBytes = Transform(protectedBytes, protect: false);
            try { return JsonSerializer.Deserialize<AuthSession>(plainBytes, JsonOptions); }
            finally { CryptographicOperations.ZeroMemory(plainBytes); }
        }
        catch (CryptographicException)
        {
            // A damaged or unavailable user-protected session should never block launcher startup.
            try { File.Delete(path); } catch (IOException) { }
            return null;
        }
        catch (Win32Exception)
        {
            try { File.Delete(path); } catch (IOException) { }
            return null;
        }
        catch (JsonException)
        {
            try { File.Delete(path); } catch (IOException) { }
            return null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static void Save(string environmentName, AuthSession session)
    {
        Directory.CreateDirectory(RootPath);
        var plainBytes = JsonSerializer.SerializeToUtf8Bytes(session);
        byte[] protectedBytes;
        try { protectedBytes = Transform(plainBytes, protect: true); }
        finally { CryptographicOperations.ZeroMemory(plainBytes); }

        var path = GetPath(environmentName);
        var temporaryPath = path + ".tmp";
        File.WriteAllBytes(temporaryPath, protectedBytes);
        CryptographicOperations.ZeroMemory(protectedBytes);
        File.Move(temporaryPath, path, true);
    }

    public static void Delete(string environmentName)
    {
        try
        {
            var path = GetPath(environmentName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string GetPath(string environmentName)
    {
        var safeName = string.Equals(environmentName, "Public", StringComparison.OrdinalIgnoreCase) ? "public" : "local";
        return Path.Combine(RootPath, $"auth-{safeName}.dat");
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        if (input.Length == 0) return Array.Empty<byte>();
        var inputPointer = Marshal.AllocHGlobal(input.Length);
        Marshal.Copy(input, 0, inputPointer, input.Length);
        var inputBlob = new DataBlob { Size = input.Length, Data = inputPointer };
        DataBlob outputBlob = default;

        try
        {
            var success = protect
                ? CryptProtectData(ref inputBlob, "1.61ONLINE launcher session", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out outputBlob);

            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not protect or unlock the saved launcher session.");
            var output = new byte[outputBlob.Size];
            Marshal.Copy(outputBlob.Data, output, 0, outputBlob.Size);
            return output;
        }
        finally
        {
            Marshal.FreeHGlobal(inputPointer);
            if (outputBlob.Data != IntPtr.Zero) _ = LocalFree(outputBlob.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
