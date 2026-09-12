using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gci.App.Services;
using Gci.Core.Models;

namespace Gci.App.ViewModels;

public sealed partial class ProfileViewModel : ObservableObject
{
    private readonly ProfileStore _store;

    public ProfileViewModel(ProfileStore store)
    {
        _store = store;
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
    }

    [RelayCommand]
    private void Clear()
    {
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
