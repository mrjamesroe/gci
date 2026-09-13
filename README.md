# GCI — Georgia Cannabis Inventory

A Windows desktop app that pulls the live online menus of Georgia's medical cannabis dispensaries into one searchable
list, refreshes them on a timer, and notifies you (Windows toast, optionally your phone) when products you watch
appear, restock, drop in price, run low, or sell out.

## Install

1. Download **gci.exe** from the [latest release](https://github.com/mrjamesroe/gci/releases/latest). It's a single
   self-contained file; nothing else to install. Windows 10 (1809+) or 11, 64-bit.
2. Put it somewhere permanent, e.g. `Documents\GCI\gci.exe`, and run it.
3. The exe isn't code-signed, so Windows SmartScreen shows "Windows protected your PC" the first time: click
   **More info → Run anyway**.

GCI checks this repository for new releases at startup and daily. When one is available, a banner offers
**Install update**: the new exe is downloaded, verified against the release's published SHA-256 checksum, swapped in
place of the old one, and restarted. Watches, settings, history and patient details live in `%LOCALAPPDATA%\GCI`
and carry over. Updating can be turned off under **Settings → Updates**.

## What it reads

All of these menus are public; no login or patient ID is needed to read them.

| Operator | Stores | Menu platform | Stock detail |
|---|---|---|---|
| Trulieve | Marietta, Dunwoody, Macon, Newnan, Pooler, Evans, Columbus | Trulieve's Adobe Commerce GraphQL (`Store` header per location) | exact units + production batches |
| Botanical Sciences | Chamblee, Marietta, Pooler, Stockbridge, West Midtown + ~30 partner pharmacies (Lotus Farmacy, AAH, …) | Mosaic (`api.mosaic.green`) | exact units |
| Fine Fettle | Athens, Decatur, Evans, Smyrna | Jane (public Algolia index) | in stock / not (Jane hides counts) |
| True Bliss | Downtown, Buckhead | Dutchie (persisted GraphQL) | exact units |
| Treevana Remedy | Milledgeville | Sweed | exact units |

Trulieve and Botanical Sciences store lists are discovered automatically each day; new stores are announced and
dispensaries are auto-monitored. Partner pharmacies are off by default (except Lotus Farmacy): turn them on in **Stores**.

Jane and Dutchie sit behind Cloudflare bot rules that fingerprint the connection and reject .NET's. When a host answers
with a Cloudflare block page, GCI escalates automatically and remembers what worked:

1. Windows' built-in `C:\Windows\System32\curl.exe`, which Cloudflare accepts on most PCs. Older Windows 10 builds of
   curl can't decompress responses, so GCI checks `curl -V` and only asks for compressed responses when it can.
2. If curl is blocked too (it depends on the PC and network) or can't run (some antivirus stops apps launching it),
   a hidden **Microsoft Edge WebView2**. It loads a blank page on the menu API's own site and reads the menu from
   there, exactly as the store's website does, with Edge's own connection and cookies. If Cloudflare shows a
   "checking your browser" page, WebView2 opens the site's home page invisibly and lets the check finish. It starts
   only when needed and closes after 3 idle minutes. The WebView2 Runtime comes with Windows 11 and up-to-date
   Windows 10; if a monitored store needs it and it's missing, GCI shows a banner with a **Download WebView2** button
   (Microsoft's small installer) and an **I've installed it** button that checks again and refreshes.

If a store is still blocked after all that, its **Stores** status says so; a VPN, proxy or network filter is the usual
cause. `GCI_NO_CURL=1` skips step 1, to test step 2 on a PC where curl works.

## Change detection

Each refresh is compared with the last good one per store:

- **New** — never seen at that store before
- **Back in stock** — seen before, was gone, is back
- **Restocked** — still listed, but a new production batch appeared (Trulieve; the alert includes its THC %) or the unit
  count jumped by at least 5 units and 25%
- **Price drop / Price up**, **Sold out**, and **Low stock** (per-watch threshold)

A store that fails to load, or suddenly returns an empty menu, is treated as an error, never as "everything sold out".
The first refresh of a store is a silent baseline.

## Watches

A watch matches on any mix of keywords (comma-separated, any one matches; words inside a keyword must all appear, so
`live rosin` doesn't match "live resin"), category, operator, specific stores and max price, and chooses which change
kinds notify. Right-click a product in **Inventory** to watch it at that store, anywhere, or its whole category.

Categories are normalized across platforms: Flower, Vape, Concentrate, Edible, Sublingual, Capsule, Oral, Topical,
Accessory, Apparel. A statewide "Concentrate" watch catches crumble/badder/rosin drops from any operator.

## News

The **News** tab follows [The Peach Scout](https://www.peachscout.com) (Georgia patient reviews and market news) via
its RSS feed, `https://www.peachscout.com/blog-feed.xml`, checked with every refresh. New posts get a NEW badge, an
unread count on the tab, and a notification. Posts mentioning one of your watch keywords (e.g. "crumble") get a ★ and
always notify, even for feeds with "Notify on every post" off. Any other RSS/Atom feed can be added from the same tab.
A feed's first read is a silent baseline, and a failed read keeps the posts already collected.

## Product images

Inventory shows a thumbnail per product (hover for a larger preview; toggle in **Settings**). Thumbnails download
lazily as rows scroll into view, using each platform's resizing CDN where available (Botanical Sciences 480px, Jane
"medium", Dutchie `images.dutchie.com`, Sweed `?width=`), and are stored as ~13 KB JPEGs in `images\thumbs`. Identical
pictures (the same product at several stores) share one file.

Keeping them honest when stores change pictures:

- **Changed picture, same link.** Each thumbnail remembers its *original* image's `ETag`/`Last-Modified`. Every
  12 hours (or via **Check all images now**) the original is re-checked with a conditional request: `304` keeps the
  thumbnail; a new version is downloaded **from the original** and re-thumbnailed locally, so a resize that a CDN cached
  for a year can't hide the change. On first download the CDN URL carries the original's version (`?v=…`) for the
  same reason.
- **Rotated link.** GCI tracks which image each product uses; when a store points a product at a new URL, the new
  image is fetched and the old one expires after a week unused.
- **Removed picture.** `404`/`410` drops the thumbnail.
- **Bad responses.** Error pages, non-image bodies and undecodable files never replace a good thumbnail; failed first
  downloads back off and retry.
- **EXIF orientation** is applied, so photos stored sideways with a rotate tag display upright; transparent PNGs are
  flattened onto white.

## Preorder

Georgia dispensary orders are pickup preorders: no payment online, just who's picking up. **🛒 Preorder in GCI** (right-click
a product in **Inventory** or **Changes**, or the **Preorder** button on a watch alert) opens the store's own website in a
GCI window and gets you to checkout fast:

- **Trulieve**: GCI clicks *Add to Bag* for the right store and goes to checkout. Checkout needs your Trulieve sign-in; enter
  it on the page once and the window remembers it (it keeps its own Edge WebView2 profile in `webview-shop\`; GCI never
  sees or stores your password).
- **Botanical Sciences and partner pharmacies (Lotus Farmacy, …)**: GCI clicks *Add to Cart* and opens the cart. If the site
  asks whether you're a medical patient, you answer; GCI waits and never adds the item twice.
- **Fine Fettle, True Bliss, Treevana**: the product page opens; add it to your cart and check out as usual.

On every store, GCI fills your saved patient details (name, date of birth, registry card number and expiry, phone,
email) into the checkout form as it appears and outlines each filled field in green. It never overwrites what's already
there, never touches sign-in forms or address, license, promo or payment fields, and only fills forms that ask for at
least two patient details (**Fill my details** fills the current page on request). **You review the order and place it
on the page; GCI never submits anything**, and it doesn't answer eligibility questions or CAPTCHAs for you.

Details are sent only to top-level HTTPS pages on the store sites GCI reads (trulieve.com, botanicalsciences.com,
iheartjane.com, dutchie.com, treevanaremedy.com, or the product's own site). Without WebView2, Preorder opens the product
in your browser instead.

## Patient details

The **Patient** tab stores your name, date of birth, registry card number and card dates, phone and email, encrypted with
Windows DPAPI (your Windows account only) in `profile.dat`. None of the menus need them. The only place they go is into a
store's checkout form in the Preorder window, when you open it, and only you submit that form. GCI also uses the expiry
date to remind you daily in the last 30 days.

## Privacy

GCI asks once whether you'd like to share **anonymous usage statistics**. Nothing is collected or sent until you
answer, and you can change your mind anytime under **Settings → Usage statistics** (turning it off discards anything
not yet sent). **Settings → See exactly what's been sent** opens a local log of every event.

If you opt in, events go to [Aptabase](https://aptabase.com), a privacy-focused analytics service. Aptabase derives
country and state from the connection and computes an anonymous visitor hash, but doesn't store IP addresses. GCI
sends:

| Event | What it contains |
|---|---|
| `app_started`, `session_ended` | GCI version, Windows version/build, Edge WebView2 major version (or none), CPU count, RAM, screen size and scaling, language, time zone, dark mode, where the exe lives (a category like "documents", never a path), settings (refresh interval, notifications on/off, whether phone push is set up), counts of watches/stores/feeds, whether a patient profile exists and a card-expiry bucket (e.g. "under 30 days"), install week and launch count |
| `daily_summary` | Once a day: which stores you monitor (one flag per store), how many watches and what kinds, items in stock by category, refresh counts/timings/failures, which menu hosts needed curl or WebView2, alerts sent, news and image-cache counts |
| `alert_sent`, `product_opened`, `store_menu_opened` | The store, operator, product name, brand, size, price and stock level (all from the stores' public menus) |
| `watch_saved`, `watch_deleted`, `watch_toggled` | Category, operator, store scope, price/low-stock settings, and **only recognized product terms** from keywords ("crumble", "live rosin", …); anything else you type is counted, never sent. "Watch this product" sends the product's public name |
| `news_opened`, `news_alert_sent`, `feed_added`/`removed` | The feed name or website host and post title |
| `tab_viewed`, `filter_used`, `search_used`, `setting_changed`, `tray_action`, `toast_clicked` | Which screen, filter value, or setting (searches are counted; search text is never sent) |
| `platform_error`, crash reports | The menu platform and an error category; crash reports carry the error type and stack trace with your Windows username, profile path, emails and long numbers removed |
| `webview2_download_clicked`, `webview2_rechecked` | That the WebView2 download button was used, and whether the runtime was found afterwards |
| `preorder_opened`, `preorder_step`, `preorder_filled`, `preorder_browser_fallback` | Store, operator and platform, where Preorder was opened from, how many patient fields are saved (a count), how far the add-to-cart got ("added", "sign-in", …), and which kinds of fields were filled ("email", "birthDate", …), never their values |
| `update_*`, `app_upgraded` | Version numbers and whether updating succeeded |
| `phone_setup_*`, `phone_nudge_dismissed` | Whether phone alerts were set up, whether the test push worked, and whether the server is the public ntfy.sh (never the topic name) |

**Never sent to Aptabase:** your patient details (name, date of birth, card number, card dates, phone, email, caregiver), watch names or any
free text you type, search text, your ntfy topic, file paths, or anything that identifies you or your PC. Volume is
capped at 150 events per day per install.

## Phone alerts

Desktop notifications only help while you're at your PC, and drops can sell out overnight. **Settings → Set up phone
alerts** (or the reminder that appears once you have a watch) sends watch alerts to your phone through the free
[ntfy](https://ntfy.sh) app:

1. Install ntfy ([iPhone](https://apps.apple.com/us/app/ntfy/id1625396347),
   [Android](https://play.google.com/store/apps/details?id=io.heckel.ntfy)); no account needed.
2. GCI creates a private, random topic. **Android:** scan the QR code and ntfy subscribes. **iPhone:** tap + in ntfy
   and enter the topic name shown (there's a Copy button).
3. **Send test notification** to confirm.

Every alert has an **Order now** button (and tapping the notification does the same) that opens the product page for
that store; Trulieve links pre-select the store. The link is also the last line of the alert text, since the iPhone
app doesn't show action buttons. Sold-out alerts get a **View** button instead. Only product and store information is
sent; anyone who knows the topic name can read it, so keep it private (**Make a new topic** replaces it). Self-hosted
ntfy servers work too: paste the topic URL into the Settings box.

GCI can't buy for you: checkout needs your own logged-in store account, and Trulieve requires your medical ID on
file (no guest checkout). Set that account up ahead of time so an alert can become an order in under a minute.

## Build & run

Requires the .NET 8 SDK on Windows 10/11.

```bash
dotnet build Gci.sln
dotnet test tests/Gci.Core.Tests
dotnet run --project src/Gci.App
```

Standalone single-file build (no .NET install needed on the target PC) → `publish\gci.exe`:

```bash
dotnet publish src/Gci.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

App options: `--minimized` (start in the tray), `--data <folder>` (use a separate data folder, handy for testing; one
copy runs per data folder, so a test profile can run alongside your normal one).

### Releasing

Bump nothing by hand: the tag is the version. Push an annotated tag and the [Build workflow](.github/workflows/build.yml)
tests, publishes `gci.exe` with `-p:Version=<tag>`, writes `gci.exe.sha256`, and creates the GitHub release using the
tag message as release notes:

```bash
git tag -a v1.0.1 -m "What changed in this version"
```

```bash
git push origin v1.0.1
```

Running copies see the new release within a day (or immediately via **Settings → Updates → Check now**).

### Command line

`gci-cli` uses the same engine and data folder:

```bash
dotnet run --project src/Gci.Cli -- stores
dotnet run --project src/Gci.Cli -- menu marietta
dotnet run --project src/Gci.Cli -- find crumble --category Concentrate
dotnet run --project src/Gci.Cli -- refresh
```

## Data folder

`%LOCALAPPDATA%\GCI`: `settings.json`, `watches.json`, `state.json` (last menu per store), `changes.json` (history),
`stores.json`, `feeds.json` (followed feeds, posts, read state), `profile.dat`, `images\` (thumbnail cache +
`index.json` of versions and product→image links), `webview\` (Edge WebView2 profile, only created if a menu needed
it), `webview-shop\` (the Preorder window's Edge profile, with any store sign-ins), `mosaic-batch-sample.json`
(written once if a Botanical Sciences product ever exposes batch detail, so batch-level restock detection can be wired to it), `telemetry.json` (unsent events, only if you opted in) and
`telemetry-log.jsonl` (what was sent).

### When a menu platform changes

Endpoints, API keys and store ids live in [`src/Gci.Core/providers.default.json`](src/Gci.Core/providers.default.json).
Copy it to `%LOCALAPPDATA%\GCI\providers.json` and edit it to fix things without rebuilding, for example:

- **Dutchie errors mentioning "persisted query"**: open a True Bliss menu in a browser, find the `FilteredProducts`
  request in DevTools → Network, and copy its `sha256Hash` into `filteredProductsHash`.
- **A new Fine Fettle / True Bliss / Treevana store**: add its Jane store id, Dutchie cName, or Sweed store id.

## Layout

```
src/Gci.Core     models, providers (Trulieve, Mosaic, Jane, Dutchie, Sweed), diff + watch engine, image cache, storage
src/Gci.App      WPF app: tray icon, toasts, scheduler, thumbnails, UI
src/Gci.Cli      headless CLI
tests/           xUnit: categories, change detection, watch matching, Trulieve mapping, image cache, thumbnail rendering
```
