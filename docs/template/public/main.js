// The logo on the home page and the README page is a <picture> whose dark source is chosen by prefers-color-scheme,
// which follows the browser's theme. The site's own theme switch sets data-bs-theme on <html> instead, so the logo
// follows that: each source whose media names a color scheme applies only when the site's theme is that scheme.
// The navbar's logo follows it too.
function followSiteTheme() {
  const dark = document.documentElement.getAttribute('data-bs-theme') === 'dark'
  for (const source of document.querySelectorAll('picture > source')) {
    if (!source.dataset.colorScheme) {
      const scheme = /prefers-color-scheme:\s*(dark|light)/.exec(source.media)
      if (!scheme) {
        continue
      }
      source.dataset.colorScheme = scheme[1]
    }
    source.media = (source.dataset.colorScheme === 'dark') === dark ? 'all' : 'not all'
  }

  // The navbar's logo (_appLogoPath, navbar.png) has a dark twin, navbar-dark.png, beside it.
  const logo = document.getElementById('logo')
  if (logo) {
    logo.src = logo.src.replace(/navbar(-dark)?\.png$/, dark ? 'navbar-dark.png' : 'navbar.png')
  }
}

new MutationObserver(followSiteTheme).observe(document.documentElement, { attributes: true, attributeFilter: ['data-bs-theme'] })
followSiteTheme()

export default {}
