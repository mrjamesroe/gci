using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gci.Core.Models;
using Gci.Core.Services;

namespace Gci.App.ViewModels;

/// <summary>
/// State of the Preorder window: which product it's for and what's happened so far. GCI adds the item to the store's
/// bag and fills in the patient's details; the patient reviews and places the order on the page.
/// </summary>
public sealed partial class PreorderViewModel : ObservableObject
{
    private readonly Func<PrefillData> _prefill;
    private readonly TelemetryClient _telemetry;

    public PreorderViewModel(PreorderRequest request, Func<PrefillData> prefill, TelemetryClient telemetry)
    {
        _prefill = prefill;
        _telemetry = telemetry;
        Start(request);
    }

    [ObservableProperty] private PreorderRequest _request = null!;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _hintText = "";

    public string Title => $"Preorder · {Request.ProductName} · {Request.StoreName}";

    /// <summary>Raised when the patient asks GCI to fill their details into the current page.</summary>
    public event Action? FillRequested;

    public PrefillData Prefill => _prefill();

    /// <summary>Trulieve and Botanical Sciences get the item put in the bag automatically.</summary>
    public string? CartRecipe => Request.Provider switch
    {
        ProviderKind.Trulieve => "trulieve",
        ProviderKind.Mosaic => "mosaic",
        _ => null,
    };

    public void Start(PreorderRequest request)
    {
        Request = request;
        OnPropertyChanged(nameof(Title));
        StatusText = $"Opening {request.ProductName} at {request.StoreName}…";
        var fields = Prefill.FieldCount;
        HintText = fields == 0
            ? "Tip: save your name, date of birth, card number, phone and email in GCI → Patient and they'll be filled into checkout for you."
            : "";
        _telemetry.Track("preorder_opened", new Dictionary<string, object?>
        {
            ["platform"] = request.Provider.ToString(), ["operator"] = request.Operator, ["store"] = request.StoreName,
            ["source"] = request.Source, ["profile_fields"] = fields, ["adds_to_cart"] = CartRecipe is not null,
        });
    }

    public void OnPageLoaded()
    {
        if (CartRecipe is null && StatusText.StartsWith("Opening", StringComparison.Ordinal))
            StatusText = "Add it to your cart on the page and check out; GCI fills in your details as the form appears. You place the order.";
    }

    public void OnCartStep(string step)
    {
        var store = Request.StoreName;
        StatusText = step switch
        {
            "added" => $"Added to your bag at {store}. Continue to checkout on the page.",
            "sign-in" => $"Added to your bag at {store}. Sign in to {Request.Operator} on the page to check out; this window remembers your sign-in for next time.",
            "checkout" => $"Added to your bag at {store} and opened checkout. Review your details, then place the order on the page.",
            "eligibility" => "Answer the medical patient question on the page; GCI then adds the item to your cart.",
            "cart" => $"Added to your cart at {store}. Continue to checkout on the page; your details fill in as the form appears.",
            "cart-eligibility" => $"Added to your cart at {store}. Answer the medical patient question on the page, then continue to checkout.",
            "missing" => $"{Request.Operator} no longer lists this product (it may have sold out). Try the store's menu instead.",
            "unavailable" => $"{Request.Operator} shows this product as unavailable at {store} right now.",
            "not-added" => "GCI couldn't confirm it was added. Check your cart on the page.",
            _ => "Couldn't find the add-to-cart button. Add it on the page, then check out.",
        };
        _telemetry.Track("preorder_step", new Dictionary<string, object?> { ["platform"] = Request.Provider.ToString(), ["step"] = step });
    }

    public void OnFilled(IReadOnlyCollection<string> fields, bool manual)
    {
        HintText = $"Filled {fields.Count} field{(fields.Count == 1 ? "" : "s")} from your patient details (outlined in green). Check them before you place the order.";
        _telemetry.Track("preorder_filled", new Dictionary<string, object?>
        {
            ["platform"] = Request.Provider.ToString(), ["count"] = fields.Count, ["kinds"] = string.Join(",", fields.Distinct().Order()),
            ["manual"] = manual,
        });
    }

    [RelayCommand]
    private void Fill()
    {
        if (Prefill.FieldCount == 0)
        {
            HintText = "No patient details saved yet. Add them in GCI → Patient, then click Fill my details again.";
            return;
        }
        FillRequested?.Invoke();
    }

    [RelayCommand]
    private void OpenInBrowser()
    {
        _telemetry.Track("preorder_browser_fallback", new Dictionary<string, object?> { ["platform"] = Request.Provider.ToString(), ["reason"] = "button" });
        try
        {
            Process.Start(new ProcessStartInfo(Request.ProductUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't open the browser: {ex.Message}";
        }
    }
}
