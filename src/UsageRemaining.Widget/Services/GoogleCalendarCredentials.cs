using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace UsageRemaining.Widget.Services;

public sealed record GoogleOAuthConfiguration
{
    private const string InvalidConfigurationMessage = "Google 연결 설정을 확인하지 못했습니다. 앱 연결 설정을 다시 준비해야 합니다.";
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
    public string ConfigurationMessage { get; init; } = "Google 연결 준비가 아직 완료되지 않았습니다. 추가 설치는 필요하지 않습니다.";

    public static GoogleOAuthConfiguration Load(string? directory = null)
    {
        var id = Environment.GetEnvironmentVariable("CODEX_PULSE_GOOGLE_CLIENT_ID");
        var secret = Environment.GetEnvironmentVariable("CODEX_PULSE_GOOGLE_CLIENT_SECRET");
        if (!string.IsNullOrWhiteSpace(id))
            return new() { ClientId = id.Trim(), ClientSecret = secret?.Trim() ?? "", ConfigurationMessage = "Google Calendar 연결 준비가 되었습니다." };
        var paths = new[]
        {
            Path.Combine(directory ?? GoogleCalendarCredentialStore.DefaultDirectory, "google-oauth-client.json"),
            Path.Combine(AppContext.BaseDirectory, "google-oauth-client.json")
        };
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (!document.RootElement.TryGetProperty("installed", out var installed))
                    return new() { ConfigurationMessage = InvalidConfigurationMessage };
                var clientId = installed.TryGetProperty("client_id", out var clientIdNode) ? clientIdNode.GetString() : null;
                var clientSecret = installed.TryGetProperty("client_secret", out var clientSecretNode) ? clientSecretNode.GetString() : null;
                if (!string.IsNullOrWhiteSpace(clientId))
                    return new() { ClientId = clientId, ClientSecret = clientSecret ?? "", ConfigurationMessage = "Google Calendar 연결 준비가 되었습니다." };
                return new() { ConfigurationMessage = InvalidConfigurationMessage };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                return new() { ConfigurationMessage = InvalidConfigurationMessage };
            }
        }
        return new();
    }
}

public sealed record GoogleCalendarCredentials
{
    public string AccessToken { get; init; } = "";
    public string RefreshToken { get; init; } = "";
    public DateTimeOffset ExpiresAt { get; init; }
    public string AccountEmail { get; init; } = "";
    public string[] SelectedCalendarIds { get; init; } = [];
}

public interface IGoogleCalendarCredentialStore
{
    Task<GoogleCalendarCredentials?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(GoogleCalendarCredentials credentials, CancellationToken cancellationToken);
    Task ClearAsync(CancellationToken cancellationToken);
}

public sealed class GoogleCalendarCredentialStore : IGoogleCalendarCredentialStore
{
    private readonly string _path;
    public static string DefaultDirectory => Environment.GetEnvironmentVariable("USAGEREMAINING_DATA_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexPulse");

    public GoogleCalendarCredentialStore(string? directory = null)
    {
        _path = Path.Combine(directory ?? DefaultDirectory, "google-auth", "credentials.dpapi");
    }

    public async Task<GoogleCalendarCredentials?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return null;
        var encrypted = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
        var plain = Protect(encrypted, false);
        try { return JsonSerializer.Deserialize<GoogleCalendarCredentials>(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task SaveAsync(GoogleCalendarCredentials credentials, CancellationToken cancellationToken)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(credentials);
        byte[] encrypted;
        try { encrypted = Protect(plain, true); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var folder = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_path)) File.Delete(_path);
        return Task.CompletedTask;
    }

    private static byte[] Protect(byte[] input, bool encrypt)
    {
        var inputBlob = new DataBlob { Size = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        DataBlob output = default;
        try
        {
            Marshal.Copy(input, 0, inputBlob.Data, input.Length);
            var success = encrypt
                ? CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException("Google 연결 정보를 Windows 사용자 보호 저장소에서 처리하지 못했습니다.");
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            for (var i = 0; i < inputBlob.Size; i++) Marshal.WriteByte(inputBlob.Data, i, 0);
            Marshal.FreeHGlobal(inputBlob.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (var i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
