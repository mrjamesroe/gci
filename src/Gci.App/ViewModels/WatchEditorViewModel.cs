using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Gci.Core.Models;
using Gci.Core.Services;

namespace Gci.App.ViewModels;

public sealed partial class StoreChoice(StoreInfo store, bool selected, Action changed) : ObservableObject
{
    public StoreInfo Store { get; } = store;
    public string Label => Store.DisplayName;
    [ObservableProperty] private bool _isSelected = selected;
    partial void OnIsSelectedChanged(bool value) => changed();
}

/// <summary>Edits a copy of a watch rule, with a live count of what it matches right now.</summary>
public sealed partial class WatchEditorViewModel : ObservableObject
{
    private readonly IReadOnlyList<(InventoryItem Item, StoreInfo Store)> _current;
    private readonly Guid _id;

    public WatchEditorViewModel(WatchRule rule, IReadOnlyList<StoreInfo> stores, IReadOnlyList<(InventoryItem, StoreInfo)> current)
    {
        _current = current;
        _id = rule.Id;
        Categories = new[] { new Option<ProductCategory?>(null, "Any category") }
            .Concat(Enum.GetValues<ProductCategory>().Select(c => new Option<ProductCategory?>(c, c.ToString()))).ToList();
        Operators = new[] { new Option<string?>(null, "Any operator") }
            .Concat(stores.Select(s => s.Operator).Distinct().Order().Select(o => new Option<string?>(o, o))).ToList();

        Name = rule.Name;
        Enabled = rule.Enabled;
        Keywords = string.Join(", ", rule.Keywords);
        MaxPrice = rule.MaxPrice?.ToString("0.##", CultureInfo.CurrentCulture) ?? "";
        NotifyAvailable = rule.NotifyAvailable;
        NotifyRestock = rule.NotifyRestock;
        NotifySoldOut = rule.NotifySoldOut;
        NotifyPriceDrop = rule.NotifyPriceDrop;
        LowStock = rule.LowStockThreshold?.ToString(CultureInfo.CurrentCulture) ?? "";
        SelectedCategory = Categories.First(c => c.Value == rule.Category);
        SelectedOperator = Operators.FirstOrDefault(o => o.Value == rule.Operator) ?? Operators[0];
        AllStores = rule.StoreKeys.Count == 0;

        StoreChoices = stores.OrderBy(s => s.IsPharmacyPartner).ThenBy(s => s.DisplayName)
            .Select(s => new StoreChoice(s, rule.StoreKeys.Contains(s.Key), UpdatePreview)).ToList();
        UpdatePreview();
    }

    public IReadOnlyList<Option<ProductCategory?>> Categories { get; }
    public IReadOnlyList<Option<string?>> Operators { get; }
    public IReadOnlyList<StoreChoice>? StoreChoices { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _keywords = "";
    [ObservableProperty] private string _maxPrice = "";
    [ObservableProperty] private Option<ProductCategory?> _selectedCategory = null!;
    [ObservableProperty] private Option<string?> _selectedOperator = null!;
    [ObservableProperty] private bool _allStores;
    [ObservableProperty] private bool _notifyAvailable;
    [ObservableProperty] private bool _notifyRestock;
    [ObservableProperty] private bool _notifySoldOut;
    [ObservableProperty] private bool _notifyPriceDrop;
    [ObservableProperty] private string _lowStock = "";
    [ObservableProperty] private string _previewText = "";
    [ObservableProperty] private IReadOnlyList<string> _previewItems = [];

    partial void OnKeywordsChanged(string value) => UpdatePreview();
    partial void OnMaxPriceChanged(string value) => UpdatePreview();
    partial void OnSelectedCategoryChanged(Option<ProductCategory?> value) => UpdatePreview();
    partial void OnSelectedOperatorChanged(Option<string?> value) => UpdatePreview();
    partial void OnAllStoresChanged(bool value) => UpdatePreview();

    public WatchRule ToRule()
    {
        var rule = new WatchRule
        {
            Id = _id,
            Name = string.IsNullOrWhiteSpace(Name) ? "Watch" : Name.Trim(),
            Enabled = Enabled,
            Keywords = Keywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            Category = SelectedCategory.Value,
            Operator = SelectedOperator.Value,
            StoreKeys = AllStores || StoreChoices is null ? new() : StoreChoices.Where(c => c.IsSelected).Select(c => c.Store.Key).ToList(),
            MaxPrice = decimal.TryParse(MaxPrice.Trim().TrimStart('$'), NumberStyles.Number, CultureInfo.CurrentCulture, out var p) ? p : null,
            NotifyAvailable = NotifyAvailable,
            NotifyRestock = NotifyRestock,
            NotifySoldOut = NotifySoldOut,
            NotifyPriceDrop = NotifyPriceDrop,
            LowStockThreshold = int.TryParse(LowStock.Trim(), out var t) && t > 0 ? t : null,
        };
        return rule;
    }

    private void UpdatePreview()
    {
        if (StoreChoices is null) return; // still constructing
        var rule = ToRule();
        var matches = _current
            .Where(x => x.Item.InStock && WatchMatcher.Matches(rule, x.Item, x.Store))
            .ToList();
        PreviewText = matches.Count switch
        {
            0 => "Nothing matches right now. You'll be notified when something does.",
            1 => "1 item matches right now:",
            _ => $"{matches.Count} items match right now:",
        };
        PreviewItems = matches
            .OrderBy(m => m.Item.Name)
            .Take(40)
            .Select(m => $"{m.Item.Name} {m.Item.Size} · {m.Item.EffectivePrice:C0} · {m.Store.DisplayName}")
            .ToList();
    }
}
