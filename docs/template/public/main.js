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

// For a page in a menu drop-down, the template's breadcrumb lists the page, then the drop-down. The drop-down goes
// first, as text, since it has no page of its own; when the page has the drop-down's name, the drop-down is left out.
function orderBreadcrumb() {
  const list = document.querySelector('#breadcrumb ol')
  if (!list) {
    return
  }

  const dropDowns = new Set([...document.querySelectorAll('#navbar .dropdown-toggle')].map((a) => a.textContent.trim()))
  const items = [...list.children]
  const index = items.findIndex((item, i) => i > 0 && dropDowns.has(item.textContent.trim()))
  if (index < 0) {
    return
  }

  const dropDown = items[index]
  if (items[0].textContent.trim() === dropDown.textContent.trim()) {
    dropDown.remove()
    return
  }

  dropDown.textContent = dropDown.textContent.trim()
  list.prepend(dropDown)
}

const breadcrumb = document.getElementById('breadcrumb')
if (breadcrumb) {
  new MutationObserver(orderBreadcrumb).observe(breadcrumb, { childList: true, subtree: true })
}

new MutationObserver(followSiteTheme).observe(document.documentElement, { attributes: true, attributeFilter: ['data-bs-theme'] })
followSiteTheme()

export default {
  iconLinks: [
    { icon: 'github', href: 'https://github.com/AvroSharp/AvroSharp', title: 'GitHub' },
  ],
}
