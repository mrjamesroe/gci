// GCI Preorder window: puts the chosen product in the store's bag using the site's own buttons, then opens the bag or
// checkout. It never places an order; the patient does that on the page. Each step reports back to GCI.
(() => {
  if (window.top !== window || window.__gciCart) return;

  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const visible = el => !!el && el.offsetParent !== null;
  const button = re => [...document.querySelectorAll('button, a[role=button], [role=button]')]
    .find(b => visible(b) && re.test((b.innerText || '').trim()));
  const waitFor = async (probe, ms) => {
    for (const end = Date.now() + ms; Date.now() < end; await sleep(250)) {
      const found = probe();
      if (found) return found;
    }
    return null;
  };
  const report = (step, detail) => window.chrome.webview.postMessage(JSON.stringify({ type: 'gci-cart', step, detail: detail || null }));
  const notFound = () => /page not found|could not be found|no longer be available/i.test(document.title + ' ' + (document.querySelector('main')?.innerText || '').slice(0, 400));

  // Trulieve: "Add to Bag" on the product page (already set to the store by ?store=), then "Proceed to Checkout".
  // Checkout needs the patient's Trulieve sign-in, which they enter on the page.
  const trulieve = async () => {
    const add = await waitFor(() => button(/^Add to Bag/i) || (notFound() && 'missing'), 20000);
    if (add === 'missing') return report('missing');
    if (!add) return report('no-button');
    if (add.disabled || add.getAttribute('aria-disabled') === 'true') return report('unavailable');
    add.click();
    const proceed = await waitFor(() => button(/Proceed to Checkout/i), 12000);
    if (!proceed) return report('added');
    proceed.click();
    const signIn = await waitFor(() => document.querySelector('input[type=password]')?.offsetParent && 'sign-in', 5000);
    report(signIn ? 'sign-in' : 'checkout');
  };

  // Botanical Sciences / partner pharmacies (Mosaic): "Add to Cart", answering nothing on the patient's behalf.
  // If the site asks whether they're a medical patient, wait for their answer, then add once more only if the cart
  // count didn't change, so the item is never added twice.
  const mosaic = async () => {
    const count = () => {
      const m = document.querySelector('[aria-label^="cart has"]')?.getAttribute('aria-label')?.match(/cart has (\d+)/);
      return m ? parseInt(m[1], 10) : 0;
    };
    const eligibility = () => [...document.querySelectorAll('[role=dialog], .MuiDialog-root, .MuiModal-root')]
      .find(d => visible(d) && /medical\s+patient/i.test(d.innerText));
    const add = await waitFor(() => button(/^Add to Cart/i) || (notFound() && 'missing'), 20000);
    if (add === 'missing') return report('missing');
    if (!add) return report('no-button');
    const before = count();
    add.click();
    let added = await waitFor(() => count() > before, 4000);
    if (!added && eligibility()) {
      report('eligibility');
      await waitFor(() => !eligibility(), 120000);
      added = await waitFor(() => count() > before, 3000);
      if (!added) {
        button(/^Add to Cart/i)?.click();
        added = await waitFor(() => count() > before, 5000);
      }
    }
    if (!added) return report('not-added');
    const cart = document.querySelector('[aria-label^="cart has"] button') || document.querySelector('[aria-label^="cart has"]');
    cart?.click();
    // The site may ask its medical patient question when the cart opens; that answer is the patient's to give.
    report(await waitFor(eligibility, 2500) ? 'cart-eligibility' : 'cart');
  };

  window.__gciCart = { trulieve, mosaic };
})();
