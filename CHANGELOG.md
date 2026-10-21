# GCI version history

Release notes for every version of the GCI desktop app (Windows and macOS), newest first. Downloads for each
version are on the [releases page](https://github.com/mrjamesroe/gci/releases). Your watches, settings and history
carry over every update.

The web app at [gcimenu.com](https://gcimenu.com) updates continuously; see
[what's new on the web](https://gcimenu.com/whats-new.html).

---

## 1.6.8 — October 21, 2026

- **Settings → Updates:** the version number is now a link to this version history, with release notes for every
  version. The **What's new** button on the "GCI updated" notification opens it too.

## 1.6.7 — October 6, 2026

- **macOS:** Install update now closes GCI and reopens the new version on its own, instead of waiting for you to
  quit. (Updating *from* 1.6.6 still needs one manual quit; every update after this one is automatic.)

## 1.6.6 — October 5, 2026

- **Inventory: new Notes column.** A **New** or **Back** badge marks products that arrived or came back in stock this
  week, and items whose price has moved show what their history says — *Usually $40 · 13% less*, *Lowest in 30
  days*, or *Price history*. Hover for the full history.
- **Inventory: "New/back this week" filter.**
- Price history is kept on your computer for 180 days, starting from your existing change log. Prices that flicker
  for under an hour (menu glitches) are ignored.

## 1.6.5 — September 29, 2026

- **macOS:** anonymous usage statistics (if you've opted in) now actually send.
- **macOS:** releases now include checksums for the Mac downloads, so **Install update** can verify and install them.

## 1.6.4 — September 29, 2026

- **Fixed:** refreshes could get stuck with *"An item with the same key has already been added"* when a store's menu
  listed the same product twice. GCI now ignores the repeat, and an install that was already stuck recovers on its
  next refresh.
- A problem at one store no longer stops every other store from refreshing.
- **macOS:** the app reports its real version, so the update banner no longer offers the version you already have.

## 1.6.3 — September 23, 2026

- The optional header sponsor message now loads from GCI's own site, gcimenu.com.
- Documentation refresh, including the new web app at [gcimenu.com](https://gcimenu.com).

## 1.6.2 — September 22, 2026

*(Versions 1.6.0 and 1.6.1 were internal builds; their changes shipped here.)*

- **Cost per mg of THC:** a sortable **$/mg THC** column so you can compare the real cost of a dose across every store.
- **Promotion badges:** when a store runs a named deal or a discount, it's flagged.
- A brief first-run notice explaining what GCI is and isn't — an independent, informational tool, not medical
  advice, with menu data you should confirm at the store.
- A lighter styling pass across the app, and an occasional, clearly labelled sponsor message in the header that you
  can turn off in Settings.
- **macOS:** patient details are encrypted at rest (Keychain), Preorder opens in an in-app window, and the app can
  update itself.

## 1.5.8 — September 17, 2026

- Anonymous usage statistics now report the correct operating system (Mac installs were previously counted as
  Windows).

## 1.5.7 — September 16, 2026

*(Version 1.5.6 didn't publish; its changes shipped here.)*

- **Fine Fettle** stores now show item quantities in Inventory and Changes, like every other store. Low counts show
  exactly; a well-stocked item shows "30+".

## 1.5.5 — September 16, 2026

- Store refreshes cope better with slow menus (fixes occasional Trulieve timeouts on large stores).
- **Windows dark mode:** dropdowns, combo boxes and right-click menus are fully themed.

## 1.5.4 — September 14, 2026

- **Windows dark mode:** table rows no longer show black text on the dark background.

## 1.5.3 — September 14, 2026

- **Dark mode on Windows**, and a **Light / Dark / System** theme picker in Settings → Appearance on Windows and macOS.

## 1.5.2 — September 14, 2026

- **macOS:** no more lingering Dock icons or duplicate copies — one copy runs at a time, and closing the window quits.
- The Changes tab shows product pictures for older history too, not just new changes.

## 1.5.1 — September 14, 2026

- Product pictures on the Changes tab (Windows and macOS).
- **macOS:** hovering a product picture shows it full size with the name and store, like Windows.
- **macOS:** fixed "Show images" toggling the wrong column.

## 1.5.0 — September 14, 2026 — now on macOS

- **GCI runs natively on macOS** (Apple Silicon and Intel) alongside Windows: browsing, watches, changes, news,
  stores, product pictures and phone alerts.
- Follows the system light/dark appearance.

## 1.4.0 — September 14, 2026

- Usage statistics now have two tiers. A **basic, anonymous install ping** (app version, operating system version and
  a random install id — no activity or anything that identifies you) is on by default and can be turned off in
  Settings. **Detailed usage** stays opt-in. Settings → *See exactly what's been sent* shows every event.

## 1.3.1 — September 13, 2026

- Groundwork for batch-level restock alerts on Botanical Sciences and partner-pharmacy products. No visible change.

## 1.3.0 — September 13, 2026

- **Preorder:** right-click a product in Inventory or Changes and choose **Preorder in GCI**, or use the Preorder
  button on an alert. The store's own website opens in a GCI window with the item in your bag (Trulieve, Botanical
  Sciences and its partner pharmacies) and your saved patient details filled into checkout. You review the order and
  place it yourself — GCI never submits it.
- The Patient tab adds phone and email, which stores ask for with pickup orders.

## 1.2.3 — September 13, 2026

- If Fine Fettle or True Bliss menus need Microsoft Edge WebView2 and your PC doesn't have it, GCI shows a banner
  with a **Download WebView2** button and refreshes those menus once it's installed.

## 1.2.2 — September 12, 2026

- **Fixed:** Fine Fettle and True Bliss menus coming up empty on PCs with an older Windows 10 version of curl.
- Clearer store errors on the Stores tab.

## 1.2.1 — September 12, 2026

- Fine Fettle and True Bliss menus now load on PCs and networks where they came up empty: GCI falls back to reading
  them through Microsoft Edge WebView2 when needed.
- If a store still can't be read, the Stores tab explains why in plain language.

## 1.2.0 — September 12, 2026

- **Phone alerts in two minutes:** Settings → *Set up phone alerts* creates a private topic for the free ntfy app,
  shows a QR code (Android) or topic name (iPhone), and sends a test.
- Every alert has an **Order now** button that opens the product page at that store.

## 1.1.0 — September 11, 2026

- Optional anonymous usage statistics. GCI asks once; nothing is sent unless you choose to share, and you can turn it
  off anytime in Settings. It never includes your patient details, your name or anything you type.

## 1.0.0 — September 11, 2026 — first release

- Live Georgia medical cannabis menus in one Windows app: Trulieve, Botanical Sciences (and partner pharmacies like
  Lotus Farmacy), Fine Fettle, True Bliss and Treevana Remedy.
- **Watches** that notify on new items, back in stock, restocks, price drops, low stock and sell-outs — on Windows
  and optionally on your phone.
- A tray icon so GCI keeps watching in the background.
- Product pictures, a **News** tab following The Peach Scout, and an optional patient profile encrypted on your PC.
- Updates itself from GitHub after verifying each download.
