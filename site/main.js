// Main page: the stable release badge and the total download count.
(() => {
  const site = window.buffstrapSite;
  if (!site) return;

  async function showLatestRelease() {
    const release = await site.getJson(`${site.api}/releases/latest`);
    if (typeof release.tag_name !== 'string' || !/^[\w.-]{1,32}$/.test(release.tag_name)) return;
    document.querySelectorAll('[data-release-tag]').forEach((badge) => { badge.textContent = release.tag_name; });
  }

  async function showDownloads() {
    let downloads = 0;
    // Do not show a partial total if a project eventually has more pages than
    // this modest limit, or if any request fails.
    for (let page = 1; page <= 5; page += 1) {
      const releases = await site.getJson(`${site.api}/releases?per_page=100&page=${page}`);
      if (!Array.isArray(releases)) return;
      for (const release of releases) {
        if (!Array.isArray(release.assets)) return;
        for (const asset of release.assets) {
          if (!Number.isSafeInteger(asset.download_count) || asset.download_count < 0) return;
          downloads += asset.download_count;
        }
      }
      if (releases.length < 100) {
        const count = document.querySelector('[data-download-count]');
        const label = document.querySelector('[data-download-label]');
        if (count && label) {
          count.textContent = new Intl.NumberFormat().format(downloads);
          label.textContent = downloads === 1 ? 'download' : 'downloads';
        }
        return;
      }
    }
  }

  showLatestRelease().catch(() => {});
  showDownloads().catch(() => {});
})();
