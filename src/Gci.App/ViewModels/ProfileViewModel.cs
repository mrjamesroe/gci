using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gci.App.Services;
using Gci.Core.Models;
using Gci.Core.Services;

namespace Gci.App.ViewModels;

public sealed partial class ProfileViewModel : ObservableObject
{
    private readonly ProfileStore _store;
    private readonly TelemetryClient _telemetry;

    public ProfileViewModel(ProfileStore store, TelemetryClient telemetry)
    {
        _store = store;
        _telemetry = telemetry;
        Load(store.Load());
    }

    [ObservableProperty] private string _firstName = "";
    [ObservableProperty] private string _lastName = "";
    [ObservableProperty] private DateTime? _birthDate;
    [ObservableProperty] private string _registryCardNumber = "";
    [ObservableProperty] private DateTime? _cardIssued;
    [ObservableProperty] private DateTime? _cardExpires;
    [ObservableProperty] private string _caregiver = "";
    [ObservableProperty] private string _savedMessage = "";

    public PatientProfile Current => new()
    {
        FirstName = FirstName.Trim(),
        LastName = LastName.Trim(),
        BirthDate = BirthDate,
        RegistryCardNumber = RegistryCardNumber.Trim(),
        CardIssued = CardIssued,
        CardExpires = CardExpires,
        Caregiver = string.IsNullOrWhiteSpace(Caregiver) ? null : Caregiver.Trim(),
    };

    public string CardStatus
    {
        get
        {
            if (Current.DaysUntilExpiry(DateTime.Today) is not { } days) return "";
            return days switch
            {
                < 0 => $"⚠ Card expired {-days} days ago. Renew through your physician and the Georgia DPH registry.",
                <= 30 => $"⚠ Card expires in {days} days. GCI will remind you daily until it's renewed.",
                _ => $"Card valid for {days} more days.",
            };
        }
    }

    public string Greeting =>
        string.IsNullOrWhiteSpace(FirstName) ? "" : $"Hi {FirstName.Trim()} 👋";

    partial void OnCardExpiresChanged(DateTime? value) => OnPropertyChanged(nameof(CardStatus));
    partial void OnFirstNameChanged(string value) => OnPropertyChanged(nameof(Greeting));

    [RelayCommand]
    private void Save()
    {
        _store.Save(Current);
        SavedMessage = $"Saved (encrypted) at {DateTime.Now:t}";
        // Which fields are filled in, never their values.
        var p = Current;
        _telemetry.Track("patient_profile_saved", new Dictionary<string, object?>
        {
            ["name"] = p.FirstName.Length > 0 || p.LastName.Length > 0,
            ["birth_date"] = p.BirthDate is not null,
            ["card_number"] = p.RegistryCardNumber.Length > 0,
            ["card_dates"] = p.CardIssued is not null || p.CardExpires is not null,
            ["caregiver"] = p.Caregiver is not null,
        });
    }

    [RelayCommand]
    private void Clear()
    {
        _telemetry.Track("patient_profile_removed");
        _store.Delete();
        Load(new PatientProfile());
        SavedMessage = "Patient details removed from this PC.";
    }

    private void Load(PatientProfile p)
    {
        FirstName = p.FirstName;
        LastName = p.LastName;
        BirthDate = p.BirthDate;
        RegistryCardNumber = p.RegistryCardNumber;
        CardIssued = p.CardIssued;
        CardExpires = p.CardExpires;
        Caregiver = p.Caregiver ?? "";
    }
}
