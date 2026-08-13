/* RapidsolDestek mockups — shared runtime:
   i18n engine (EN/TR switcher), chrome/nav renderer, UI behaviors.
   Pages declare: <body class="portal|backoffice" data-panel="portal|agent|admin" data-nav="<navKey>">
   Page-specific translations: window.PAGE_I18N = { en: {...}, tr: {...} } before this script runs. */

(() => {
  "use strict";

  /* ---------- i18n ---------- */

  const LANG_KEY = "rd-lang";
  const dicts = {
    en: Object.assign({}, window.I18N_EN || {}, (window.PAGE_I18N || {}).en || {}),
    tr: Object.assign({}, window.I18N_TR || {}, (window.PAGE_I18N || {}).tr || {}),
  };

  let lang = localStorage.getItem(LANG_KEY) || "tr";
  if (!dicts[lang]) lang = "tr";

  const t = (key) => dicts[lang][key] ?? dicts.tr[key] ?? dicts.en[key] ?? key;

  function applyI18n(root) {
    (root || document).querySelectorAll("[data-i18n]").forEach((el) => {
      el.textContent = t(el.getAttribute("data-i18n"));
    });
    (root || document).querySelectorAll("[data-i18n-placeholder]").forEach((el) => {
      el.setAttribute("placeholder", t(el.getAttribute("data-i18n-placeholder")));
    });
    (root || document).querySelectorAll("[data-i18n-title]").forEach((el) => {
      el.setAttribute("title", t(el.getAttribute("data-i18n-title")));
    });
    (root || document).querySelectorAll("[data-i18n-label]").forEach((el) => {
      el.setAttribute("label", t(el.getAttribute("data-i18n-label")));
    });
    document.documentElement.lang = lang;
  }

  function setLang(next) {
    lang = next;
    localStorage.setItem(LANG_KEY, next);
    applyI18n();
    document.querySelectorAll("select[data-lang-switch]").forEach((s) => (s.value = next));
  }

  window.RD = { t, setLang, get lang() { return lang; } };

  /* ---------- Nav definitions ---------- */

  // Paths are relative to the page's own folder (portal/, agent/, admin/).
  const PORTAL_NAV = [
    { key: "home", href: "index.html", label: "nav.home" },
    { key: "new", href: "open.html", label: "nav.newTicket" },
    { key: "tickets", href: "tickets.html", label: "nav.myTickets" },
    { key: "kb", href: "kb.html", label: "nav.kb" },
  ];

  const AGENT_NAV = [
    { group: "nav.group.workspace", items: [
      { key: "dashboard", href: "dashboard.html", label: "nav.dashboard", icon: "▤" },
      { key: "live", href: "live.html", label: "nav.live", icon: "◉", live: true },
      { key: "tickets", href: "tickets.html", label: "nav.tickets", icon: "◫", count: "8" },
      { key: "tasks", href: "tasks.html", label: "nav.tasks", icon: "☑", count: "6" },
    ]},
    { group: "nav.group.records", items: [
      { key: "users", href: "users.html", label: "nav.users", icon: "◔" },
      { key: "orgs", href: "orgs.html", label: "nav.orgs", icon: "▣" },
      { key: "directory", href: "directory.html", label: "nav.directory", icon: "☏" },
    ]},
    { group: "nav.group.knowledge", items: [
      { key: "kb", href: "kb.html", label: "nav.kb", icon: "✎" },
      { key: "canned", href: "canned.html", label: "nav.canned", icon: "❝" },
    ]},
    { group: "nav.group.personal", items: [
      { key: "profile", href: "profile.html", label: "nav.myProfile", icon: "◍" },
    ]},
  ];

  const ADMIN_NAV = [
    { group: "nav.group.overview", items: [
      { key: "dashboard", href: "dashboard.html", label: "nav.dashboard", icon: "▤" },
      { key: "system-info", href: "system-info.html", label: "nav.systemInfo", icon: "ℹ" },
      { key: "system-logs", href: "system-logs.html", label: "nav.systemLogs", icon: "≡" },
      { key: "audit-logs", href: "audit-logs.html", label: "nav.auditLogs", icon: "◷" },
    ]},
    { group: "nav.group.settings", items: [
      { key: "settings-company", href: "settings-company.html", label: "nav.company", icon: "▣" },
      { key: "settings-system", href: "settings-system.html", label: "nav.system", icon: "⚙" },
      { key: "settings-tickets", href: "settings-tickets.html", label: "nav.tickets", icon: "◫" },
      { key: "settings-tasks", href: "settings-tasks.html", label: "nav.tasks", icon: "☑" },
      { key: "settings-agents", href: "settings-agents.html", label: "nav.agents", icon: "◍" },
      { key: "settings-users", href: "settings-users.html", label: "nav.users", icon: "◔" },
      { key: "settings-kb", href: "settings-kb.html", label: "nav.kb", icon: "✎" },
    ]},
    { group: "nav.group.manage", items: [
      { key: "helptopics", href: "helptopics.html", label: "nav.helptopics", icon: "❓" },
      { key: "queues", href: "queues.html", label: "nav.queues", icon: "☰" },
      { key: "filters", href: "filters.html", label: "nav.filters", icon: "⏚" },
      { key: "slas", href: "slas.html", label: "nav.slas", icon: "⏱" },
      { key: "schedules", href: "schedules.html", label: "nav.schedules", icon: "▦" },
      { key: "forms", href: "forms.html", label: "nav.forms", icon: "▤" },
      { key: "lists", href: "lists.html", label: "nav.lists", icon: "≔" },
      { key: "pages", href: "pages.html", label: "nav.sitePages", icon: "❐" },
      { key: "apikeys", href: "apikeys.html", label: "nav.apikeys", icon: "⚿" },
      { key: "plugins", href: "plugins.html", label: "nav.plugins", icon: "✦" },
    ]},
    { group: "nav.group.emails", items: [
      { key: "emails", href: "emails.html", label: "nav.emailAddresses", icon: "✉" },
      { key: "email-settings", href: "email-settings.html", label: "nav.emailSettings", icon: "⚙" },
      { key: "templates", href: "templates.html", label: "nav.templates", icon: "❏" },
      { key: "banlist", href: "banlist.html", label: "nav.banlist", icon: "⃠" },
      { key: "email-diagnostic", href: "email-diagnostic.html", label: "nav.emailDiagnostic", icon: "➤" },
    ]},
    { group: "nav.group.team", items: [
      { key: "staff", href: "staff.html", label: "nav.staff", icon: "◍" },
      { key: "teams", href: "teams.html", label: "nav.teams", icon: "◎" },
      { key: "roles", href: "roles.html", label: "nav.roles", icon: "⛨" },
      { key: "departments", href: "departments.html", label: "nav.departments", icon: "▥" },
    ]},
  ];

  /* ---------- Chrome renderers ---------- */

  const LOGO = `
    <span class="rc-logo-mark"></span>
    <span>RAPID<span class="rc-logo-accent">SOL</span></span>`;

  const langSelect = () => `
    <select class="rc-language" data-lang-switch aria-label="Language">
      <option value="tr">TR</option>
      <option value="en">EN</option>
    </select>`;

  function renderPortalHeader(activeKey) {
    const host = document.getElementById("rd-header");
    if (!host) return;
    host.innerHTML = `
      <header class="rc-header">
        <a class="rc-logo" href="index.html">${LOGO}</a>
        <nav class="rc-nav" aria-label="Main">
          ${PORTAL_NAV.map((item) => `
            <a class="rc-nav-button ${item.key === activeKey ? "rc-selected" : ""}"
               href="${item.href}" data-i18n="${item.label}"></a>`).join("")}
        </nav>
        ${langSelect()}
        <a class="rc-account" href="profile.html" style="text-decoration:none;color:inherit">
          <div class="rc-account-copy">Bourla Salehi<small>Ulaşım A.Ş.</small></div>
          <div class="rd-avatar">BS</div>
        </a>
      </header>`;
  }

  function renderBackofficeChrome(panel, activeKey) {
    const topbar = document.getElementById("rd-topbar");
    const sidebar = document.getElementById("rd-sidebar");
    const nav = panel === "admin" ? ADMIN_NAV : AGENT_NAV;
    if (topbar) {
      topbar.innerHTML = `
        <div class="bo-topbar">
          <a class="rc-logo" href="dashboard.html">${LOGO}</a>
          <div class="bo-search"><input type="search" data-i18n-placeholder="topbar.search"></div>
          <div class="bo-topbar-right">
            <nav class="bo-panel-switch" aria-label="Panels">
              <a href="../portal/index.html" data-i18n="panel.portal"></a>
              <a href="../agent/dashboard.html" class="${panel === "agent" ? "active" : ""}" data-i18n="panel.agent"></a>
              <a href="../admin/dashboard.html" class="${panel === "admin" ? "active" : ""}" data-i18n="panel.admin"></a>
            </nav>
            ${langSelect()}
            <div class="rd-avatar" title="Ümit Yaşar Akın">ÜA</div>
          </div>
        </div>`;
    }
    if (sidebar) {
      sidebar.innerHTML = `
        <aside class="bo-sidebar">
          ${nav.map((group) => {
            const links = group.items.map((item) => `
                <a href="${item.href}" class="${item.key === activeKey ? "active" : ""}">
                  <span aria-hidden="true">${item.icon || ""}</span>
                  <span data-i18n="${item.label}"></span>
                  ${item.live ? '<span class="rd-live-dot" style="margin-left:auto"></span>' : ""}
                  ${item.count ? `<span class="count">${item.count}</span>` : ""}
                </a>`).join("");
            // Admin nav is large: render groups as collapsible sections,
            // with the group holding the active page expanded.
            if (panel === "admin") {
              const open = group.items.some((i) => i.key === activeKey);
              return `
            <details class="bo-nav-group"${open ? " open" : ""}>
              <summary class="group-label" data-i18n="${group.group}"></summary>
              ${links}
            </details>`;
            }
            return `
            <div class="bo-nav-group">
              <div class="group-label" data-i18n="${group.group}"></div>
              ${links}
            </div>`;
          }).join("")}
        </aside>`;
    }
  }

  /* ---------- Behaviors ---------- */

  function initBehaviors() {
    // Language switcher
    document.addEventListener("change", (e) => {
      if (e.target.matches("select[data-lang-switch]")) setLang(e.target.value);
    });

    // Tabs: <div data-tabs> <button data-tab="panelId">… ; panels: .rd-tab-panel#panelId
    document.addEventListener("click", (e) => {
      const tabBtn = e.target.closest("[data-tab]");
      if (tabBtn) {
        e.preventDefault();
        const container = tabBtn.closest("[data-tabs]");
        const scope = container?.getAttribute("data-tabs-scope");
        container.querySelectorAll("[data-tab]").forEach((b) => b.classList.toggle("active", b === tabBtn));
        const panels = scope
          ? document.querySelectorAll(`[data-tab-group="${scope}"]`)
          : document.querySelectorAll(".rd-tab-panel");
        panels.forEach((p) => p.classList.toggle("active", p.id === tabBtn.getAttribute("data-tab")));
      }

      // Dialogs
      const opener = e.target.closest("[data-dialog-open]");
      if (opener) {
        e.preventDefault();
        document.getElementById(opener.getAttribute("data-dialog-open"))?.showModal();
      }
      const closer = e.target.closest("[data-dialog-close]");
      if (closer) {
        e.preventDefault();
        closer.closest("dialog")?.close();
      }

      // Rich-text editor tool buttons
      const edBtn = e.target.closest(".rd-editor-tool");
      if (edBtn) { e.preventDefault(); handleEditorAction(edBtn); }

      // Section index (settings pages)
      const idxLink = e.target.closest(".bo-section-index a");
      if (idxLink) {
        idxLink.closest(".bo-section-index").querySelectorAll("a")
          .forEach((a) => a.classList.toggle("active", a === idxLink));
      }
    });

    // Rich-text editor font/size selects
    document.addEventListener("change", (e) => {
      if (e.target.matches(".rd-editor-toolbar select[data-ed]")) handleEditorAction(e.target);
    });

    // Select-all checkboxes: <input data-check-all="scopeSelector">
    document.addEventListener("change", (e) => {
      if (e.target.matches("[data-check-all]")) {
        const scope = document.querySelector(e.target.getAttribute("data-check-all")) || document;
        scope.querySelectorAll('tbody input[type="checkbox"]').forEach((c) => (c.checked = e.target.checked));
      }
    });

    // Mock form submits: any form without action just shows a toast
    document.addEventListener("submit", (e) => {
      if (!e.target.hasAttribute("data-real")) {
        e.preventDefault();
        toast(t("common.mockSaved"));
      }
    });

    // Multi-select filters (details.rd-filter): outside click closes open menus,
    // checkbox changes update count badges + the [data-filter-chips] row.
    document.addEventListener("click", (e) => {
      document.querySelectorAll("details.rd-filter[open]").forEach((d) => {
        if (!d.contains(e.target)) d.removeAttribute("open");
      });
      const clear = e.target.closest("[data-filter-clear]");
      if (clear) {
        e.preventDefault();
        document.querySelectorAll("details.rd-filter input:checked").forEach((c) => (c.checked = false));
        syncFilters();
      }
    });
    document.addEventListener("change", (e) => {
      if (e.target.matches('details.rd-filter input[type="checkbox"]')) syncFilters();
    });
    syncFilters();
  }

  function syncFilters() {
    document.querySelectorAll("details.rd-filter").forEach((d) => {
      const n = d.querySelectorAll("input:checked").length;
      const badge = d.querySelector(".rd-filter-count");
      if (badge) { badge.textContent = n; badge.classList.toggle("has", n > 0); }
    });
    const chipsWrap = document.querySelector("[data-filter-chips]");
    if (!chipsWrap) return;
    chipsWrap.querySelectorAll(".rd-chip").forEach((c) => c.remove());
    const checked = document.querySelectorAll("details.rd-filter input:checked");
    const clearLink = chipsWrap.querySelector("[data-filter-clear]");
    checked.forEach((c) => {
      const srcLabel = c.closest("label")?.querySelector("[data-i18n]");
      if (!srcLabel) return;
      const chip = document.createElement("span");
      chip.className = "rd-chip";
      const label = document.createElement("span");
      label.setAttribute("data-i18n", srcLabel.getAttribute("data-i18n"));
      chip.append(label);
      const x = document.createElement("button");
      x.type = "button";
      x.textContent = "✕";
      x.addEventListener("click", () => { c.checked = false; syncFilters(); });
      chip.append(x);
      chipsWrap.insertBefore(chip, clearLink);
    });
    chipsWrap.hidden = checked.length === 0;
    applyI18n(chipsWrap);
  }

  /* ---------- Rich-text editor (attached to every textarea) ---------- */

  const EDITOR_TOOLS = [
    { cmd: "bold", label: "<b>B</b>", key: "editor.bold" },
    { cmd: "italic", label: "<i>I</i>", key: "editor.italic" },
    { cmd: "underline", label: "<u>U</u>", key: "editor.underline" },
    { cmd: "strike", label: "<s>S</s>", key: "editor.strike" },
    { sep: true },
    { cmd: "ul", label: "☷", key: "editor.ul" },
    { cmd: "ol", label: "1.", key: "editor.ol" },
    { sep: true },
    { cmd: "link", label: "🔗", key: "editor.link" },
    { cmd: "photo", label: "🖼", key: "editor.photo" },
    { cmd: "video", label: "🎬", key: "editor.video" },
    { cmd: "file", label: "📎", key: "editor.file" },
    { sep: true },
    { cmd: "table", label: "▦", key: "editor.table" },
    { cmd: "hr", label: "―", key: "editor.hr" },
  ];

  function enhanceTextareas() {
    document.querySelectorAll("textarea").forEach((ta) => {
      if (ta.hasAttribute("data-plain") || ta.closest(".rd-editor-wrap")) return;
      const wrap = document.createElement("div");
      wrap.className = "rd-editor-wrap";
      const bar = document.createElement("div");
      bar.className = "rd-editor-toolbar";
      bar.setAttribute("role", "toolbar");
      bar.innerHTML = `
        <select data-ed="font" data-i18n-title="editor.font">
          <option value="">Inter</option>
          <option value="Arial">Arial</option>
          <option value="Georgia">Georgia</option>
          <option value="'Courier New',monospace">Courier New</option>
        </select>
        <select data-ed="size" data-i18n-title="editor.size">
          <option value="12px">12</option>
          <option value="14px" selected>14</option>
          <option value="16px">16</option>
          <option value="18px">18</option>
        </select>
        <span class="rd-editor-separator"></span>
        ${EDITOR_TOOLS.map((tool) => tool.sep
          ? '<span class="rd-editor-separator"></span>'
          : `<button type="button" class="rd-editor-tool" data-ed="${tool.cmd}" data-i18n-title="${tool.key}">${tool.label}</button>`
        ).join("")}`;
      const bottom = document.createElement("div");
      bottom.className = "rd-editor-bottom";
      ta.parentNode.insertBefore(wrap, ta);
      wrap.appendChild(bar);
      wrap.appendChild(ta);
      wrap.appendChild(bottom);
      const count = () => { bottom.textContent = `${ta.value.length} / 50.000`; };
      ta.addEventListener("input", count);
      count();
    });
  }

  function edInsert(ta, before, after, placeholder) {
    const s = ta.selectionStart ?? ta.value.length;
    const e = ta.selectionEnd ?? ta.value.length;
    const sel = ta.value.slice(s, e) || placeholder;
    ta.value = ta.value.slice(0, s) + before + sel + (after || "") + ta.value.slice(e);
    ta.focus();
    const pos = s + before.length + sel.length + (after || "").length;
    ta.setSelectionRange(pos, pos);
    ta.dispatchEvent(new Event("input"));
  }

  function handleEditorAction(ctl) {
    const wrap = ctl.closest(".rd-editor-wrap");
    const ta = wrap?.querySelector("textarea");
    if (!ta) return;
    const cmd = ctl.getAttribute("data-ed");
    switch (cmd) {
      case "font": ta.style.fontFamily = ctl.value; break;
      case "size": ta.style.fontSize = ctl.value; break;
      case "bold": edInsert(ta, "**", "**", t("editor.phText")); break;
      case "italic": edInsert(ta, "*", "*", t("editor.phText")); break;
      case "underline": edInsert(ta, "__", "__", t("editor.phText")); break;
      case "strike": edInsert(ta, "~~", "~~", t("editor.phText")); break;
      case "ul": edInsert(ta, "\n• ", "", t("editor.phItem")); break;
      case "ol": edInsert(ta, "\n1. ", "", t("editor.phItem")); break;
      case "link": edInsert(ta, "[", "](https://)", t("editor.phLink")); break;
      case "hr": edInsert(ta, "\n――――――――――\n", "", ""); break;
      case "table":
        edInsert(ta, "\n| " + t("editor.phCol") + " 1 | " + t("editor.phCol") + " 2 |\n| --- | --- |\n|  |  |\n", "", "");
        break;
      case "photo": case "video": case "file":
        toast(t("editor.mockPicker"));
        break;
    }
  }

  function toast(message) {
    let el = document.getElementById("rd-toast");
    if (!el) {
      el = document.createElement("div");
      el.id = "rd-toast";
      el.style.cssText =
        "position:fixed;bottom:24px;left:50%;transform:translateX(-50%);" +
        "background:var(--rd-petrol);color:#fff;padding:11px 19px;border-radius:11px;" +
        "font-size:13px;z-index:99;box-shadow:0 10px 30px rgba(0,0,0,.25);transition:opacity .3s";
      document.body.appendChild(el);
    }
    el.textContent = message;
    el.style.opacity = "1";
    clearTimeout(el._hide);
    el._hide = setTimeout(() => (el.style.opacity = "0"), 2200);
  }
  window.RD.toast = toast;

  /* ---------- Boot ---------- */

  function boot() {
    const body = document.body;
    const panel = body.getAttribute("data-panel");
    const activeKey = body.getAttribute("data-nav");
    if (panel === "portal") renderPortalHeader(activeKey);
    if (panel === "agent" || panel === "admin") renderBackofficeChrome(panel, activeKey);
    enhanceTextareas();
    initBehaviors();
    applyI18n();
    document.querySelectorAll("select[data-lang-switch]").forEach((s) => (s.value = lang));
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", boot);
  else boot();
})();
