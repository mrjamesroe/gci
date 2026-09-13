// GCI Preorder window: fills the patient's saved details into a store's checkout form.
// Runs in top-level documents only. The details arrive from GCI by message, and GCI sends them only to HTTPS pages on
// store sites. Nothing here submits anything: the patient reviews the form and places the order.
(() => {
  if (window.top !== window || window.__gciPrefill) return;

  const filled = new WeakSet();   // fields GCI filled (never refilled, so a patient's edit sticks)
  let data = null;
  let observer = null;
  let timer = 0;

  const text = el => [
    el.name, el.id, el.getAttribute('autocomplete'), el.placeholder, el.getAttribute('aria-label'),
    el.getAttribute('data-testid'), el.labels && [...el.labels].map(l => l.innerText).join(' '),
    el.getAttribute('aria-labelledby') && el.getAttribute('aria-labelledby').split(' ')
      .map(id => document.getElementById(id)?.innerText || '').join(' '),
  ].filter(Boolean).join(' ').toLowerCase().replace(/[_\-]+/g, ' ');

  // Which saved detail a field wants, or null. Specific kinds are checked before names.
  const kindOf = el => {
    const t = text(el);
    const type = (el.type || '').toLowerCase();
    if (!t && type !== 'email' && type !== 'tel') return null;
    if (/password|search|coupon|promo|referr|credit|\bcc\b|cvv|cvc|card ?holder|security code/.test(t)) return null;
    if (type === 'email' || /e ?mail/.test(t)) return 'email';
    if (type === 'tel' || /phone|mobile|cell|\btel\b/.test(t)) return 'phone';
    if (/zip|postal|address|street|city|driver|licen[cs]e|state id|caregiver/.test(t)) return null;
    if (/birth|\bdob\b|bday/.test(t)) {
      // A whole date (date input, "date" label, or an MM/DD/YYYY pattern) vs. one part of a split month/day/year.
      const whole = type === 'date' || /date|mm ?\/ ?dd|dd ?\/ ?mm|mm dd|dd mm/.test(t);
      if (!whole) {
        if (/month|\bmm\b/.test(t)) return 'birthMonth';
        if (/year|yyyy|\byy\b/.test(t)) return 'birthYear';
        if (/\bday\b|\bdd\b/.test(t)) return 'birthDay';
      }
      return 'birthDate';
    }
    const cardish = /medical|registry|mmj|patient|marijuana|cannabis|low thc|recommendation|certification/.test(t);
    if (cardish && /exp/.test(t)) return 'cardExpires';
    if (cardish && /card|number|\bid\b|#|no\b/.test(t)) return 'cardNumber';
    if (/first ?name|given ?name|fname|^first$/.test(t) || /\bfirst\b/.test(t) && /name/.test(t)) return 'firstName';
    if (/last ?name|family ?name|surname|lname|^last$/.test(t) || /\blast\b/.test(t) && /name/.test(t)) return 'lastName';
    if (/\bname\b/.test(t) && !/user|company|business|store|product|nick|display|pet|middle/.test(t)) return 'fullName';
    return null;
  };

  const valueFor = (kind, el) => {
    if (!data) return null;
    const type = (el.type || '').toLowerCase();
    switch (kind) {
      case 'birthDate': return type === 'date' ? data.birthDateIso : data.birthDateUs;
      case 'cardExpires': return type === 'date' ? data.cardExpiresIso : data.cardExpiresUs;
      case 'phone': return data.phone && el.maxLength > 0 && el.maxLength <= 10 ? data.phoneDigits : data.phone;
      default: return data[kind] ?? null;
    }
  };

  const fillable = el => {
    if (filled.has(el) || el.disabled || el.readOnly) return false;
    const type = (el.type || '').toLowerCase();
    if (['hidden', 'password', 'submit', 'button', 'checkbox', 'radio', 'file', 'search', 'image', 'reset'].includes(type)) return false;
    if (el.tagName !== 'SELECT' && el.value) return false; // never overwrite what's there
    const box = el.getBoundingClientRect();
    return box.width > 0 && box.height > 0;
  };

  const setValue = (el, value) => {
    if (el.tagName === 'SELECT') {
      const want = String(value).toLowerCase();
      const option = [...el.options].find(o => o.value.toLowerCase() === want || o.text.trim().toLowerCase() === want
        || (/^\d+$/.test(want) && parseInt(o.value, 10) === parseInt(want, 10)));
      if (!option || el.value === option.value) return false;
      value = option.value;
    }
    const proto = el.tagName === 'SELECT' ? HTMLSelectElement.prototype
      : el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, value); // works with React-controlled inputs
    for (const name of ['input', 'change', 'blur']) el.dispatchEvent(new Event(name, { bubbles: true }));
    el.style.outline = '2px solid #2e7d32';
    el.style.outlineOffset = '1px';
    return true;
  };

  // Fills recognized fields. Automatic fills only touch forms that ask for at least two patient details, so a lone
  // newsletter box isn't filled; sign-in forms (anything with a password field) are always left alone.
  const fill = manual => {
    if (!data) return [];
    const groups = new Map();
    for (const el of document.querySelectorAll('input, select, textarea')) {
      const kind = kindOf(el);
      if (!kind || !fillable(el)) continue;
      const group = el.form || el.closest('[role=dialog], section, fieldset') || document.body;
      if (!groups.has(group)) groups.set(group, []);
      groups.get(group).push([el, kind]);
    }
    const done = [];
    for (const [group, fields] of groups) {
      if (group.querySelector('input[type=password]')) continue;
      if (!manual && new Set(fields.map(f => f[1])).size < 2) continue;
      for (const [el, kind] of fields) {
        const value = valueFor(kind, el);
        if (value && setValue(el, value)) { filled.add(el); done.push(kind); }
      }
    }
    if (done.length) window.chrome.webview.postMessage(JSON.stringify({ type: 'gci-filled', fields: done, manual: !!manual }));
    return done;
  };

  const watch = () => {
    if (observer) return;
    observer = new MutationObserver(() => { clearTimeout(timer); timer = setTimeout(() => fill(false), 400); });
    observer.observe(document.documentElement, { childList: true, subtree: true });
  };

  window.__gciPrefill = { fill, kindOf };

  window.chrome?.webview?.addEventListener('message', e => {
    const msg = e.data;
    if (!msg || msg.type !== 'gci-fill') return;
    data = msg.data;
    fill(!!msg.manual);
    watch();
  });

  const ready = () => window.chrome?.webview?.postMessage(JSON.stringify({ type: 'gci-ready' }));
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', ready, { once: true });
  else ready();
})();
