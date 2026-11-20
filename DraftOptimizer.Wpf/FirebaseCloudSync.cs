using DraftOptimizer.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;

namespace DraftOptimizer;

internal sealed partial class FirebaseCloudSync : IDisposable
{
    private const string ApiKey = "AIzaSyCxM-jtZNWBQmwcCsB8CfbY-mz4hNk1R7I";
    private const string ProjectId = "draftoptimizer";
    private const string CredentialName = "DraftOptimizer.Firebase.RefreshToken";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private string? _idToken;
    private string? _refreshToken;
    private DateTimeOffset _expiresAt;

    public string? UserId { get; private set; }
    public string? Email { get; private set; }
    public bool IsSignedIn => !string.IsNullOrWhiteSpace(UserId);

    public async Task<bool> RestoreSessionAsync(CancellationToken ct)
    {
        var saved = CredentialVault.Read();
        if (saved == null) return false;
        var (uid, refreshToken) = saved.Value;
        try
        {
            await RefreshAsync(refreshToken, uid, ct);
            return true;
        }
        catch (FirebaseAuthenticationException)
        {
            CredentialVault.Delete();
            ClearSession();
            return false;
        }
    }

    public Task SignInAsync(string email, string password, CancellationToken ct) => AuthenticateAsync("signInWithPassword", email, password, ct);
    public Task RegisterAsync(string email, string password, CancellationToken ct) => AuthenticateAsync("signUp", email, password, ct);

    private async Task AuthenticateAsync(string operation, string email, string password, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(
            $"https://identitytoolkit.googleapis.com/v1/accounts:{operation}?key={ApiKey}",
            new { email, password, returnSecureToken = true }, ct);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!response.IsSuccessStatusCode) throw AuthError(body.RootElement);
        var root = body.RootElement;
        UserId = root.GetProperty("localId").GetString();
        Email = root.TryGetProperty("email", out var emailValue) ? emailValue.GetString() : email;
        _idToken = root.GetProperty("idToken").GetString();
        _refreshToken = root.GetProperty("refreshToken").GetString();
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(int.Parse(root.GetProperty("expiresIn").GetString() ?? "3600"));
        if (string.IsNullOrWhiteSpace(UserId) || string.IsNullOrWhiteSpace(_refreshToken)) throw new InvalidDataException("Firebase devolvió una sesión incompleta.");
        CredentialVault.Save(UserId, _refreshToken);
    }

    private static Exception AuthError(JsonElement root)
    {
        var code = root.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message)
            ? message.GetString() ?? "AUTH_ERROR" : "AUTH_ERROR";
        return new FirebaseAuthenticationException(code switch
        {
            "EMAIL_EXISTS" => "Ese correo ya tiene una cuenta.",
            "EMAIL_NOT_FOUND" or "INVALID_PASSWORD" or "INVALID_LOGIN_CREDENTIALS" => "Correo o contraseña incorrectos.",
            "WEAK_PASSWORD : Password should be at least 6 characters" => "La contraseña debe tener al menos 6 caracteres.",
            "OPERATION_NOT_ALLOWED" => "Activa Email/Password en Firebase Authentication.",
            "TOO_MANY_ATTEMPTS_TRY_LATER" => "Firebase bloqueó temporalmente los intentos. Prueba más tarde.",
            _ => $"Firebase Authentication: {code}"
        });
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_idToken) || DateTimeOffset.UtcNow >= _expiresAt.AddMinutes(-2))
        {
            if (string.IsNullOrWhiteSpace(_refreshToken) || string.IsNullOrWhiteSpace(UserId)) throw new InvalidOperationException("La sesión de Firebase ha caducado. Inicia sesión de nuevo.");
            await RefreshAsync(_refreshToken, UserId, ct);
        }
    }

    private async Task RefreshAsync(string refreshToken, string uid, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken
        });
        using var response = await _http.PostAsync($"https://securetoken.googleapis.com/v1/token?key={ApiKey}", content, ct);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!response.IsSuccessStatusCode) throw AuthError(body.RootElement);
        var root = body.RootElement;
        _idToken = root.GetProperty("id_token").GetString();
        _refreshToken = root.GetProperty("refresh_token").GetString();
        UserId = root.TryGetProperty("user_id", out var userId) ? userId.GetString() : uid;
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(int.Parse(root.GetProperty("expires_in").GetString() ?? "3600"));
        if (root.TryGetProperty("email", out var emailValue)) Email = emailValue.GetString();
        if (string.IsNullOrWhiteSpace(Email))
        {
            using var accountResponse = await _http.PostAsJsonAsync(
                $"https://identitytoolkit.googleapis.com/v1/accounts:lookup?key={ApiKey}", new { idToken = _idToken }, ct);
            accountResponse.EnsureSuccessStatusCode();
            using var account = await JsonDocument.ParseAsync(await accountResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            Email = account.RootElement.GetProperty("users")[0].GetProperty("email").GetString();
        }
        if (!string.IsNullOrWhiteSpace(UserId) && !string.IsNullOrWhiteSpace(_refreshToken)) CredentialVault.Save(UserId, _refreshToken);
    }

    public async Task<CloudDocument?> LoadAsync(CancellationToken ct)
    {
        await EnsureTokenAsync(ct);
        using var request = AuthenticatedRequest(HttpMethod.Get, DocumentUrl());
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!response.IsSuccessStatusCode) throw FirestoreError(body.RootElement);
        var root = body.RootElement;
        string? payload = null;
        if (root.TryGetProperty("fields", out var fields) && fields.TryGetProperty("payload", out var payloadField))
        {
            if (!payloadField.TryGetProperty("stringValue", out var value) ||
                value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new InvalidDataException("El documento de sincronización de Firebase contiene un payload no válido.");
            payload = value.GetString()!;
        }
        var updateTime = root.TryGetProperty("updateTime", out var time) ? time.GetString() : null;
        return new(payload, updateTime);
    }

    public async Task<string?> SaveAsync(string payload, CancellationToken ct, string? expectedUpdateTime = null, bool expectAbsent = false)
    {
        await EnsureTokenAsync(ct);
        var url = DocumentUrl() + "?updateMask.fieldPaths=payload";
        if (expectedUpdateTime != null) url += "&currentDocument.updateTime=" + Uri.EscapeDataString(expectedUpdateTime);
        else if (expectAbsent) url += "&currentDocument.exists=false";
        var data = new { fields = new Dictionary<string, object> { ["payload"] = new { stringValue = payload } } };
        using var request = AuthenticatedRequest(HttpMethod.Patch, url);
        request.Content = JsonContent.Create(data);
        using var response = await _http.SendAsync(request, ct);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!response.IsSuccessStatusCode)
        {
            if (body.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("status", out var status) &&
                status.GetString() is "FAILED_PRECONDITION" or "ABORTED")
                throw new CloudConflictException();
            throw FirestoreError(body.RootElement);
        }
        return body.RootElement.TryGetProperty("updateTime", out var time) ? time.GetString() : null;
    }

    private HttpRequestMessage AuthenticatedRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _idToken);
        return request;
    }

    private string DocumentUrl() => $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents/users/{Uri.EscapeDataString(UserId ?? throw new InvalidOperationException("No hay sesión."))}/sync/main";

    private static Exception FirestoreError(JsonElement root)
    {
        var message = root.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var value) ? value.GetString() : null;
        return new InvalidOperationException("Firestore: " + (message ?? "no se pudo completar la solicitud."));
    }

    public void SignOut()
    {
        CredentialVault.Delete();
        ClearSession();
    }

    private void ClearSession() { UserId = null; Email = null; _idToken = null; _refreshToken = null; _expiresAt = default; }
    public void Dispose() => _http.Dispose();

    internal sealed record CloudDocument(string? Payload, string? UpdateTime);
    internal sealed class CloudConflictException() : InvalidOperationException("El documento de la nube cambió durante la sincronización.") { }
    private sealed class FirebaseAuthenticationException(string message) : InvalidOperationException(message) { }

    private static class CredentialVault
    {
        public static void Save(string uid, string token)
        {
            var bytes = Encoding.Unicode.GetBytes(token);
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var credential = new NativeCredential
                {
                    Type = 1, TargetName = CredentialName, UserName = uid, Persist = 2,
                    CredentialBlobSize = (uint)bytes.Length, CredentialBlob = pin.AddrOfPinnedObject()
                };
                if (!CredWrite(ref credential, 0)) throw new InvalidOperationException("Windows no pudo guardar la sesión protegida de Firebase.");
            }
            finally { pin.Free(); Array.Clear(bytes); }
        }

        public static (string Uid, string Token)? Read()
        {
            if (!CredRead(CredentialName, 1, 0, out var pointer)) return null;
            try
            {
                var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
                if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0 || credential.UserName == null) return null;
                var bytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                try { return (credential.UserName, Encoding.Unicode.GetString(bytes)); }
                finally { Array.Clear(bytes); }
            }
            finally { CredFree(pointer); }
        }

        public static void Delete() { if (CredDelete(CredentialName, 1, 0)) return; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeCredential
        {
            public uint Flags; public uint Type; public string? TargetName; public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten; public uint CredentialBlobSize;
            public IntPtr CredentialBlob; public uint Persist; public uint AttributeCount; public IntPtr Attributes;
            public string? TargetAlias; public string? UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredWrite(ref NativeCredential credential, uint flags);
        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredDelete(string target, uint type, uint flags);
        [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = false)] private static extern void CredFree(IntPtr credential);
    }
}

internal sealed class CloudProfileSnapshot
{
    public List<BuildProfile> Profiles { get; set; } = [];
    public FavoriteSettings Favorites { get; set; } = new();
    public HashSet<Guid> DeletedProfileIds { get; set; } = [];
}
