// Pre-release page: shows the newest GitHub pre-release test build.
(() => {
  const site = window.buffstrapSite;
  if (!site) return;

  const repository = 'bufferclick/BuffStrap';
  const assetName = 'BuffStrap-alpha-win-x64.zip';
  const releasesPage = `https://github.com/${repository}/releases`;
  const tagPattern = /^v\d+\.\d+\.\d+-[0-9A-Za-z.-]+$/;
  const status = document.querySelector('[data-preview-status]');

  // Only accept the expected test-build zip from this repository's own download
  // path. Pre-releases are never offered as a normal release anywhere else.
  function readPreview(release) {
    if (!release || release.draft !== false || release.prerelease !== true) return null;
    if (typeof release.tag_name !== 'string' || !tagPattern.test(release.tag_name)) return null;
    if (!Array.isArray(release.assets)) return null;
    const assets = release.assets.filter((asset) => asset && asset.name === assetName);
    if (assets.length !== 1) return null;
    const expected = `https://github.com/${repository}/releases/download/${release.tag_name}/${assetName}`;
    if (assets[0].browser_download_url !== expected) return null;
    if (!Number.isSafeInteger(assets[0].size) || assets[0].size <= 0) return null;
    return { release, asset: assets[0] };
  }

  function formatDate(value) {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return '—';
    return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' }).format(date);
  }

  function megabytes(bytes) {
    return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
  }

  function setText(selector, text) {
    document.querySelectorAll(selector).forEach((node) => { node.textContent = text; });
  }

  function show(preview) {
    const { release, asset } = preview;
    setText('[data-preview-tag]', release.tag_name);
    setText('[data-preview-date]', formatDate(release.published_at));
    setText('[data-preview-size]', megabytes(asset.size));
    const downloads = Number.isSafeInteger(asset.download_count) ? asset.download_count : 0;
    setText('[data-preview-downloads]', new Intl.NumberFormat().format(downloads));
    document.querySelectorAll('[data-preview-download]').forEach((link) => {
      link.href = asset.browser_download_url;
    });
    document.querySelectorAll('[data-preview-release-link]').forEach((link) => {
      link.href = `${releasesPage}/tag/${encodeURIComponent(release.tag_name)}`;
    });

    const notesBox = document.querySelector('[data-preview-notes-box]');
    const notes = document.querySelector('[data-preview-notes]');
    const body = typeof release.body === 'string' ? release.body.trim() : '';
    if (notesBox && notes) {
      if (body) {
        notes.textContent = body.length > 4000 ? body.slice(0, 4000).trimEnd() + '…' : body;
        notesBox.hidden = false;
      } else {
        notesBox.hidden = true;
      }
    }
    if (status) status.textContent = 'Test builds are not stable releases. Unzip and run BuffStrap.exe.';
  }

  function showUnavailable() {
    setText('[data-preview-tag]', 'No test build right now');
    setText('[data-preview-date]', '—');
    setText('[data-preview-size]', '—');
    setText('[data-preview-downloads]', '—');
    if (status) {
      status.textContent = 'There is no downloadable pre-release at the moment. Check the GitHub releases page, or use the stable release.';
    }
  }

  async function loadPreview() {
    const releases = await site.getJson(`${site.api}/releases?per_page=40`);
    if (!Array.isArray(releases)) throw new Error('Unexpected release list');
    // The API lists the newest release first.
    for (const release of releases) {
      const preview = readPreview(release);
      if (preview) {
        show(preview);
        return;
      }
    }
    showUnavailable();
  }

  async function showStableTag() {
    const release = await site.getJson(`${site.api}/releases/latest`);
    if (typeof release.tag_name !== 'string' || !/^v\d+\.\d+\.\d+$/.test(release.tag_name)) return;
    setText('[data-stable-tag]', `(${release.tag_name})`);
  }

  loadPreview().catch(() => {
    showUnavailable();
    if (status) status.textContent = 'Could not load test build details from GitHub. Use the GitHub releases page instead.';
  });
  showStableTag().catch(() => {});
})();
