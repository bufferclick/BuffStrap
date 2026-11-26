// Set the theme before the stylesheet paints to avoid a flash on reload.
(function () {
  var theme = 'dark';
  try {
    var saved = localStorage.getItem('buffstrap-site-theme');
    if (saved === 'light' || saved === 'dark') {
      theme = saved;
    } else if (window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches) {
      theme = 'light';
    }
  } catch (_) {
    // The site still works when storage is unavailable.
  }
  document.documentElement.setAttribute('data-theme', theme);
})();
