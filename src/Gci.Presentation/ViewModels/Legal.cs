namespace Gci.App.ViewModels;

/// <summary>
/// The disclaimer/consent text shown once on first run (and again if <see cref="Version"/> changes), plus the
/// standing legal + privacy summary shown on the Settings tab. Kept in one place so the Windows and macOS apps show
/// identical wording. Bump <see cref="Version"/> whenever the disclaimer text materially changes to re-prompt users.
/// </summary>
public static class Legal
{
    /// <summary>Stamp stored in settings once accepted; change it to force re-acceptance after a material edit.</summary>
    public const string Version = "2026-09-22";

    public const string Title = "Before you use GCI";

    public const string PrivacyUrl = "https://github.com/mrjamesroe/gci#privacy";
    public const string RegistryUrl = "https://dph.georgia.gov/low-thc-oil-registry";

    /// <summary>The must-read points, shown on first run and repeated in Settings. Plain language for patients.</summary>
    public const string Disclaimer =
        "GCI (Georgia Cannabis Inventory) is an independent tool that reads the public online menus of Georgia's " +
        "licensed low-THC medical cannabis dispensaries and shows them in one place. Please read this before you rely on it.\n\n" +

        "Not affiliated. GCI is not affiliated with, endorsed by, or operated by any dispensary, producer, platform, or " +
        "the State of Georgia. All product names, prices, and brands belong to their owners.\n\n" +

        "Menu data can be wrong or out of date. Listings come from stores' own websites and can be delayed, incomplete, " +
        "or mistaken. A product shown here may be sold out, priced differently, or unavailable in person. Always confirm " +
        "with the dispensary before you travel or rely on anything you see here.\n\n" +

        "Not medical, legal, or purchasing advice. GCI does not recommend products and is not a substitute for your " +
        "physician or pharmacist. Cannabis products have not been evaluated by the FDA. Talk to your doctor about your " +
        "treatment.\n\n" +

        "For registered Georgia patients only. Buying low-THC oil in Georgia requires a valid Low THC Oil Registry card. " +
        "GCI does not sell, deliver, or facilitate any purchase, and it is not a way to obtain cannabis without a card.\n\n" +

        "Preorder just fills in a store's own form. When you use Preorder, GCI opens the dispensary's own website and " +
        "pre-fills the checkout from the details you entered. You review and submit it yourself — GCI never places an " +
        "order for you.\n\n" +

        "Your privacy stays on this device. GCI runs on your computer. The menus it reads are public. Any patient " +
        "details you enter are encrypted and stored only on this device (used solely to pre-fill a store's checkout that " +
        "you submit). GCI never sends your patient details anywhere. Optional, anonymous usage statistics can be turned " +
        "off in Settings.\n\n" +

        "No warranty. GCI is provided “as is,” without warranty of any kind, and you use it at your own risk.";

    /// <summary>One-line agreement shown next to the Accept control on first run.</summary>
    public const string AcceptLabel =
        "I understand GCI is an independent, informational tool, that menu data may be inaccurate, and that this is not medical advice.";
}
