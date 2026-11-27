// Shared site behaviour: theme toggle, copy button, mobile menu and GitHub helpers.
// Both the main page and the pre-release page load this file first.
(() => {
  const root = document.documentElement;
  const themeButton = document.querySelector('[data-theme-toggle]');
  const themeKey = 'buffstrap-site-theme';

  function updateThemeButton() {
    if (!themeButton) return;
    const next = root.dataset.theme === 'light' ? 'dark' : 'light';
    const label = `Switch to ${next} mode`;
    themeButton.setAttribute('aria-label', label);
    themeButton.setAttribute('title', label);
    const themeColor = document.querySelector('meta[name="theme-color"]');
    if (themeColor) themeColor.content = root.dataset.theme === 'light' ? '#fbfafb' : '#101012';
  }

  updateThemeButton();
  themeButton?.addEventListener('click', () => {
    const next = root.dataset.theme === 'light' ? 'dark' : 'light';
    root.dataset.theme = next;
    try { localStorage.setItem(themeKey, next); } catch (_) { /* Optional preference. */ }
    updateThemeButton();
  });

  const command = document.querySelector('#install-command');
  const copyButton = document.querySelector('[data-copy-command]');
  const copyLabel = document.querySelector('[data-copy-label]');
  const copyStatus = document.querySelector('[data-copy-status]');
  let copyReset;

  async function copyText(text) {
    if (navigator.clipboard && window.isSecureContext) {
      try {
        await navigator.clipboard.writeText(text);
        return true;
      } catch (_) { /* Try the older browser fallback. */ }
    }
    const field = document.createElement('textarea');
    field.value = text;
    field.setAttribute('readonly', '');
    field.style.cssText = 'position:fixed;left:-9999px;top:0;opacity:0';
    document.body.appendChild(field);
    field.select();
    let copied = false;
    try { copied = document.execCommand('copy'); } catch (_) { /* Selection fallback below. */ }
    field.remove();
    return copied;
  }

  copyButton?.addEventListener('click', async () => {
    if (!command || !copyLabel || !copyStatus) return;
    const copied = await copyText(command.textContent.trim());
    if (copied) {
      copyLabel.textContent = 'Copied!';
      copyStatus.textContent = 'Download command copied to clipboard.';
    } else {
      const selection = window.getSelection();
      if (selection) {
        const range = document.createRange();
        range.selectNodeContents(command);
        selection.removeAllRanges();
        selection.addRange(range);
      }
      copyLabel.textContent = 'Select';
      copyStatus.textContent = 'Select and copy the highlighted command.';
    }
    window.clearTimeout(copyReset);
    copyReset = window.setTimeout(() => { copyLabel.textContent = 'Copy'; }, 2500);
  });

  const mobileMenu = document.querySelector('.mobile-menu');
  mobileMenu?.querySelectorAll('a').forEach((link) => {
    link.addEventListener('click', () => { mobileMenu.open = false; });
  });
  document.addEventListener('click', (event) => {
    if (mobileMenu?.open && !mobileMenu.contains(event.target)) mobileMenu.open = false;
  });
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && mobileMenu?.open) {
      mobileMenu.open = false;
      mobileMenu.querySelector('summary')?.focus();
    }
  });

  // GitHub is optional: every page works without it. No repository star
  // statistics are requested.
  const api = 'https://api.github.com/repos/bufferclick/BuffStrap';
  async function getJson(url) {
    const response = await fetch(url, { headers: { Accept: 'application/vnd.github+json' } });
    if (!response.ok) throw new Error('GitHub data unavailable');
    return response.json();
  }

  window.buffstrapSite = { api, getJson };
})();
