using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gci.Core.Models;
using Gci.Core.Services;

namespace Gci.App.Services;

/// <summary>
/// Keeps the patient profile in profile.dat, encrypted with Windows DPAPI for the current user,
/// so it's unreadable to other accounts and off this PC.
/// </summary>
public sealed class ProfileStore(DataStore data)
{
    private const string FileName = "profile.dat";
    private static readonly byte[] Entropy = "GCI.PatientProfile.v1"u8.ToArray();

    public PatientProfile Load()
    {
        var bytes = data.LoadBytes(FileName);
        if (bytes is null) return new PatientProfile();
        try
        {
            var json = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<PatientProfile>(Encoding.UTF8.GetString(json), DataStore.Options) ?? new();
        }
        catch (CryptographicException)
        {
            return new PatientProfile();
        }
    }

    public void Save(PatientProfile profile)
    {
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profile, DataStore.Options));
        data.SaveBytes(FileName, ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser));
    }

    public void Delete() => data.Delete(FileName);
}
