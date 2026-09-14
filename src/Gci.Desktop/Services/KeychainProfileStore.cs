using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gci.App.Services;
using Gci.Core.Models;
using Gci.Core.Services;

namespace Gci.Desktop.Services;

/// <summary>
/// The macOS counterpart of the WPF DPAPI ProfileStore. The patient profile is AES-256-GCM encrypted to profile.enc
/// in the data folder; the random key is kept in the login Keychain via <c>/usr/bin/security</c>. So the card details
/// never touch a command line (only the opaque key does), and the file alone is useless without the Keychain key.
///
/// NOTE: untested on macOS from this machine. The Keychain CLI may show a one-time "security wants to use your
/// confidential information" prompt on first read; that's expected for an unsigned app. On non-macOS (e.g. running the
/// Avalonia app on Windows for testing) the security tool is absent, so this degrades to no persistence.
/// </summary>
public sealed class KeychainProfileStore(DataStore data) : IProfileStore
{
    private const string FileName = "profile.enc";
    private const string Service = "GCI";
    private const string Account = "gci-patient-profile-key";
    private const int NonceLen = 12, TagLen = 16;

    public PatientProfile Load()
    {
        try
        {
            if (GetKey() is not { } key || data.LoadBytes(FileName) is not { } blob || blob.Length < NonceLen + TagLen)
                return new PatientProfile();

            var nonce = blob.AsSpan(0, NonceLen);
            var tag = blob.AsSpan(NonceLen, TagLen);
            var cipher = blob.AsSpan(NonceLen + TagLen);
            var plain = new byte[cipher.Length];
            using var gcm = new AesGcm(key, TagLen);
            gcm.Decrypt(nonce, cipher, tag, plain);
            return JsonSerializer.Deserialize<PatientProfile>(Encoding.UTF8.GetString(plain), DataStore.Options) ?? new();
        }
        catch (Exception)
        {
            return new PatientProfile(); // no key, wrong key, tampered file, or not on macOS
        }
    }

    public void Save(PatientProfile profile)
    {
        var key = EnsureKey();
        if (key is null) return; // Keychain unavailable (e.g. not macOS); nothing persisted

        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profile, DataStore.Options));
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagLen];
        using (var gcm = new AesGcm(key, TagLen))
            gcm.Encrypt(nonce, plain, cipher, tag);

        var blob = new byte[NonceLen + TagLen + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceLen);
        cipher.CopyTo(blob, NonceLen + TagLen);
        data.SaveBytes(FileName, blob);
    }

    public void Delete()
    {
        data.Delete(FileName);
        RunSecurity("delete-generic-password", "-a", Account, "-s", Service);
    }

    /// <summary>The stored 256-bit key, or null if there isn't one (or the Keychain isn't reachable).</summary>
    private static byte[]? GetKey()
    {
        if (RunSecurity("find-generic-password", "-a", Account, "-s", Service, "-w") is not { } b64) return null;
        try { return Convert.FromBase64String(b64.Trim()); }
        catch (FormatException) { return null; }
    }

    /// <summary>The stored key, creating one on first use; null if the Keychain can't be reached.</summary>
    private static byte[]? EnsureKey()
    {
        if (GetKey() is { } existing) return existing;
        var key = RandomNumberGenerator.GetBytes(32);
        // -U updates the item if it already exists, so a racing create doesn't fail.
        return RunSecurity("add-generic-password", "-a", Account, "-s", Service, "-w", Convert.ToBase64String(key), "-U") is not null
            ? key
            : null;
    }

    /// <summary>Runs /usr/bin/security and returns its stdout on success, or null on any failure (tool absent, non-zero exit).</summary>
    private static string? RunSecurity(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/security")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var process = Process.Start(psi);
            if (process is null) return null;
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? stdout : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
