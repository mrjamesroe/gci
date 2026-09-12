namespace Gci.Core.Models;

/// <summary>
/// The patient's Georgia Low THC Oil Registry details. Stored encrypted on this PC only;
/// none of the menus GCI reads require it, so it is never sent anywhere.
/// </summary>
public sealed class PatientProfile
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public DateTime? BirthDate { get; set; }
    public string RegistryCardNumber { get; set; } = "";
    public DateTime? CardIssued { get; set; }
    public DateTime? CardExpires { get; set; }
    /// <summary>Optional caregiver name when purchases are made on the patient's behalf.</summary>
    public string? Caregiver { get; set; }
    public string? PreferredStoreKey { get; set; }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(FirstName) && string.IsNullOrWhiteSpace(LastName) &&
        string.IsNullOrWhiteSpace(RegistryCardNumber) && BirthDate is null;

    public int? DaysUntilExpiry(DateTime today) =>
        CardExpires is { } exp ? (int)(exp.Date - today.Date).TotalDays : null;
}
