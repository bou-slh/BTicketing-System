# RapidsolDestek mockup conventions

Every page MUST follow these rules. Canonical examples: `portal/open.html` (portal),
and after they exist, `agent/tickets.html` / `admin/settings-tickets.html` (backoffice).

## Page skeleton

Portal pages (`portal/*.html`):

```html
<!doctype html>
<html lang="tr">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>RapidsolDestek — <Turkish page name></title>
<link rel="stylesheet" href="../assets/css/base.css">
<link rel="stylesheet" href="../assets/css/portal.css">
<script src="../assets/js/i18n-en.js"></script>
<script src="../assets/js/i18n-tr.js"></script>
<script>
window.PAGE_I18N = { tr: { /* page keys */ }, en: { /* page keys */ } };
</script>
<script src="../assets/js/app.js" defer></script>
</head>
<body class="portal" data-panel="portal" data-nav="<navKey>">
<div id="rd-header"></div>
<main class="rc-main"> …content… </main>
</body>
</html>
```

Agent/Admin pages (`agent/*.html`, `admin/*.html`) differ only in:

```html
<link rel="stylesheet" href="../assets/css/portal.css"> <!-- keep: logo + shared rc-* bits -->
<link rel="stylesheet" href="../assets/css/admin.css">
…
<body class="backoffice" data-panel="agent|admin" data-nav="<navKey>">
<div id="rd-topbar"></div>
<div id="rd-sidebar"></div>
<main class="bo-main"> …content… </main>
</body>
```

`app.js` renders the chrome into `#rd-header` / `#rd-topbar` + `#rd-sidebar` based on
`data-panel` + `data-nav`. Valid `data-nav` keys are defined in `assets/js/app.js`
(PORTAL_NAV / AGENT_NAV / ADMIN_NAV) — use the matching key so the item highlights.
Auth pages (login/pwreset/offline) have NO chrome: omit the placeholder divs, center a
`.rc-auth-card` in `.rc-auth-wrap`, and include a manual `<select class="rc-language" data-lang-switch>`.

## i18n (mandatory)

- Default language is Turkish; EN via the header switcher. Every UI label uses
  `data-i18n="key"` (or `data-i18n-placeholder` / `data-i18n-title`) and the key exists in
  BOTH `PAGE_I18N.tr` and `PAGE_I18N.en`. Element content is filled by JS — leave it empty.
- Common keys already exist globally (see `assets/js/i18n-en.js`): `common.*` (save, cancel,
  addNew, delete, enable, disable, export, more, advanced, notes, mockSaved…), `status.*`,
  `priority.*`, `nav.*`, `panel.*`, `topbar.search`. Use them; don't redefine.
- Page keys are namespaced: `"lb.assignToMe"`, `"st.helpdeskName"`, etc.
- Sample DATA stays hardcoded Turkish (names like Ümit Akın, Merve Çetin, Deniz Kaya,
  companies Ulaşım A.Ş., Tosyalı Holding, Konecta, RapidSol; subjects about payroll/bordro,
  izin, vardiya). Do not translate sample data.

## Components (use these, never invent parallel ones)

- Buttons: `.rc-button-primary`, `.rd-btn`, `.rd-btn-danger`, `.rd-btn-ghost`, `.rd-btn-sm`.
- Forms: `.rd-field` wrapper → `.rd-label` + `.rd-control` + optional `.rd-help` helper line.
  EVERY admin setting field gets a `.rd-help` one-liner explaining it in plain language.
- Toggles: `.rd-switch` (label > input[type=checkbox] + span.track + span.rd-switch-copy>b+small).
- Checkbox: `.rd-check`. Status pills: `.rd-pill .rd-pill-{open|wait|test|solved|closed|overdue|teal}`.
- Cards: `.rd-surface` + `.rd-surface-head` + `.rd-surface-body`; stat cards `.rd-stat`.
- Multi-select filter (list toolbars): `<details class="rd-filter">` → `<summary>` (label span +
  `b.rd-filter-count`) + `.rd-filter-menu` of `.rd-check` labels. Active selections render as chips in
  a `.rd-filter-chips[data-filter-chips]` row (placed right after the toolbar, `hidden` by default)
  containing an `<a data-filter-clear>` link. Counts/chips/outside-click-close are wired globally in app.js.
- Lists: `.rd-list-toolbar` (search input + filters + `.spacer` + actions), `.rd-surface` with
  `.rd-table` inside (`.col-check` first column with `data-check-all` header checkbox,
  `th.sortable`, one `th.sorted-desc`), `.rd-pagination` footer.
  Include ONE empty-state variant per list page inside a hidden `.rd-tab-panel` or as a
  commented block using `.rd-empty` (icon + h3 + p + primary button).
- Tabs: `<div class="rd-tabs" data-tabs><button class="active" data-tab="panel-x">…` with
  panels `.rd-tab-panel(.active)#panel-x`.
- Advanced settings: `<details class="rd-advanced"><summary data-i18n="common.advanced"></summary>
  <div class="rd-advanced-body">…fields…</div></details>` — put rarely-used osTicket fields here.
- Settings pages (admin): `.bo-page-head` (h1+p), then `.bo-settings` = `.bo-section-index`
  (sticky anchor links) + `.bo-settings-sections` of `.rd-surface.bo-section#section-id`s.
- Alert/permission groups: `.bo-toggle-card` (head with b+small and `.rd-switch` on the right,
  `.recipients` row of `.rd-check`es below).
- Detail info: `.bo-def` with `.row` (label span + value). Builder rows: `.bo-rule-row`.
- Dialogs: `<dialog class="rd-dialog" id="dlg-x">` + `.rd-dialog-head` (title + button.close
  with `data-dialog-close`) + `.rd-dialog-body` + `.rd-dialog-foot`; open via
  `<button data-dialog-open="dlg-x">`.
- Toast: forms without `data-real` auto-toast "mock saved" on submit — just use `<form>`.

## Content fidelity

Admin pages must represent EVERY osTicket field listed in the plan
(`/Users/rapidsolbilisim/.claude/plans/now-we-want-to-snazzy-duckling.md`) — nothing dropped,
but layered: common fields visible, rare ones inside `.rd-advanced`. Keep pages honest:
links between pages must point at real files from the gallery list (`mockups/index.html`).
