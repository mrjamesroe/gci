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

Jane and Dutchie sit behind Cloudflare rules that reject .NET's TLS handshake, so GCI automatically routes those
requests through Windows' built-in `C:\Windows\System32\curl.exe`.

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

## Patient details

The **Patient** tab stores your name, date of birth, registry card number and card dates, encrypted with Windows DPAPI
(your Windows account only) in `profile.dat`. None of the menus need them, so GCI never sends them anywhere; it uses the
expiry date to remind you daily in the last 30 days.

## Privacy

GCI asks once whether you'd like to share **anonymous usage statistics**. Nothing is collected or sent until you
answer, and you can change your mind anytime under **Settings → Usage statistics** (turning it off discards anything
not yet sent). **Settings → See exactly what's been sent** opens a local log of every event.

If you opt in, events go to [Aptabase](https://aptabase.com), a privacy-focused analytics service. Aptabase derives
country and state from the connection and computes an anonymous visitor hash, but doesn't store IP addresses. GCI
sends:

| Event | What it contains |
|---|---|
| `app_started`, `session_ended` | GCI version, Windows version/build, CPU count, RAM, screen size and scaling, language, time zone, dark mode, where the exe lives (a category like "documents", never a path), settings (refresh interval, notifications on/off, whether phone push is set up), counts of watches/stores/feeds, whether a patient profile exists and a card-expiry bucket (e.g. "under 30 days"), install week and launch count |
| `daily_summary` | Once a day: which stores you monitor (one flag per store), how many watches and what kinds, items in stock by category, refresh counts/timings/failures, alerts sent, news and image-cache counts |
| `alert_sent`, `product_opened`, `store_menu_opened` | The store, operator, product name, brand, size, price and stock level (all from the stores' public menus) |
| `watch_saved`, `watch_deleted`, `watch_toggled` | Category, operator, store scope, price/low-stock settings, and **only recognized product terms** from keywords ("crumble", "live rosin", …); anything else you type is counted, never sent. "Watch this product" sends the product's public name |
| `news_opened`, `news_alert_sent`, `feed_added`/`removed` | The feed name or website host and post title |
| `tab_viewed`, `filter_used`, `search_used`, `setting_changed`, `tray_action`, `toast_clicked` | Which screen, filter value, or setting (searches are counted; search text is never sent) |
| `platform_error`, crash reports | The menu platform and an error category; crash reports carry the error type and stack trace with your Windows username, profile path, emails and long numbers removed |
| `update_*`, `app_upgraded` | Version numbers and whether updating succeeded |

**Never sent:** your patient details (name, date of birth, card number, card dates, caregiver), watch names or any
free text you type, search text, your ntfy topic, file paths, or anything that identifies you or your PC. Volume is
capped at 150 events per day per install.

## Phone notifications (optional)

Install the [ntfy](https://ntfy.sh) app, subscribe to a hard-to-guess topic, and paste its URL (e.g.
`https://ntfy.sh/gci-yourname-4821`) in **Settings**. Only product/store info is sent.

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

App options: `--minimized` (start in the tray), `--data <folder>` (use a separate data folder, handy for testing).

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
`index.json` of versions and product→image links), `telemetry.json` (unsent events, only if you opted in) and
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
