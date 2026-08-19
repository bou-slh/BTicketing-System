// RapidsolDestek shared front-end behaviors.
// Ported from mockups/assets/js/app.js initBehaviors/enhanceTextareas/toast —
// chrome rendering and i18n stay server-side (layouts + resx), only the
// interactive primitives live here. Semantics must match the mockups 1:1
// (same data-* contracts) so ported pages behave like their twins.
"use strict";
(() => {
  /* ---------- Language switcher (server round-trip, S2) ---------- */
  document.addEventListener("change", (e) => {
    const sel = e.target.closest("select[data-lang-switch]");
    if (sel && sel.form) sel.form.submit();
  });

  /* ---------- B3 switch gating: input[data-gates="selector"] ----------
     A master switch disables every form control inside its target container(s)
     while unchecked (admin settings pages: effort master switch, alert
     bo-toggle-card recipients). Selects join the contract via
     select[data-gates] + data-gates-on="value": targets are enabled only while
     the selected value matches (B3 "auth-backend and format selects reveal
     their dependent inputs" — admin/staff-edit se-backend gates the password
     controls). Synced on change and at boot. */
  function syncGates() {
    document.querySelectorAll("input[data-gates],select[data-gates]").forEach((sw) => {
      const off = sw.tagName === "SELECT"
        ? sw.value !== sw.getAttribute("data-gates-on")
        : !sw.checked;
      document.querySelectorAll(sw.getAttribute("data-gates")).forEach((el) => {
        el.classList.toggle("rd-gated-off", off);
        const controls = el.matches("input,select,textarea,button")
          ? [el]
          : [...el.querySelectorAll("input,select,textarea,button")];
        controls.forEach((c) => {
          if (c === sw) return;
          // Controls the server rendered disabled (mockup parity) stay disabled.
          if (off) { if (!c.disabled) { c.disabled = true; c.dataset.gatedOff = "1"; } }
          else if (c.dataset.gatedOff) { c.disabled = false; delete c.dataset.gatedOff; }
        });
      });
    });
  }
  document.addEventListener("change", (e) => {
    if (e.target.matches("input[data-gates],select[data-gates]")) syncGates();
    // Radio masters (settings-company logo default/custom): checking the OTHER
    // radio of a gating radio's group never fires change on the gating radio
    // itself — resync whenever any radio sharing its name group changes.
    else if (e.target.type === "radio" && e.target.name &&
             document.querySelector(`input[data-gates][type="radio"][name="${CSS.escape(e.target.name)}"]`))
      syncGates();
  });

  /* ---------- B3 tri-state matrix masters: input[data-check-master="selector"] ----------
     A group master (role-edit / staff-edit permission cards) toggles every child
     checkbox the selector matches; child changes sync the master back to
     checked (all) / indeterminate (some) / unchecked (none). Masters are pure UI —
     only the children post. */
  function syncMasters() {
    document.querySelectorAll("input[data-check-master]").forEach((master) => {
      const kids = [...document.querySelectorAll(master.getAttribute("data-check-master"))];
      if (!kids.length) return;
      const on = kids.filter((k) => k.checked).length;
      master.checked = on === kids.length;
      master.indeterminate = on > 0 && on < kids.length;
    });
  }
  document.addEventListener("change", (e) => {
    const master = e.target.closest("input[data-check-master]");
    if (master) {
      document.querySelectorAll(master.getAttribute("data-check-master"))
        .forEach((k) => { if (!k.disabled) k.checked = master.checked; });
      master.indeterminate = false;
      return;
    }
    if (e.target.matches('input[type="checkbox"]')
        && [...document.querySelectorAll("input[data-check-master]")]
          .some((m) => e.target.matches(m.getAttribute("data-check-master"))))
      syncMasters();
  });

  /* ---------- B4 member roster (admin/teams dialog): [data-roster] ----------
     Container holds the rows ([data-roster-rows]), a <select data-roster-select>
     of addable staff and a [data-roster-add] button which clones the container's
     <template data-roster-tpl> — [data-roster-value] inputs get the option's value
     (staff id), [data-roster-name] the option's text, .rd-avatar the initials.
     The row's ✕ (shared .bo-rule-row .remove handler below) restores the option. */
  document.addEventListener("click", (e) => {
    const add = e.target.closest("[data-roster-add]");
    if (!add) return;
    e.preventDefault();
    const box = add.closest("[data-roster]");
    const sel = box?.querySelector("select[data-roster-select]");
    const opt = sel?.selectedOptions[0];
    const tpl = box?.querySelector("template[data-roster-tpl]");
    if (!box || !opt || !opt.value || !tpl) return;
    const row = tpl.content.firstElementChild.cloneNode(true);
    const name = opt.textContent.trim();
    row.setAttribute("data-roster-staff", opt.value);
    row.querySelectorAll("[data-roster-value]").forEach((i) => { i.value = opt.value; });
    const nameEl = row.querySelector("[data-roster-name]");
    if (nameEl) nameEl.textContent = name;
    const avatar = row.querySelector(".rd-avatar");
    if (avatar) {
      const words = name.split(/\s+/).filter(Boolean);
      avatar.textContent = ((words[0]?.[0] || "") + (words.length > 1 ? words[words.length - 1][0] : ""))
        .toLocaleUpperCase("tr");
    }
    box.querySelector("[data-roster-rows]")?.append(row);
    opt.hidden = true;
    opt.disabled = true;
    sel.selectedIndex = 0;
  });

  /* ---------- B9 scroll-spy: settings section rail follows the scroll ----------
     The mockups only click-highlight (.bo-section-index a) — scroll desyncs, the
     ROADMAP B9 spec adds real spying: the last .bo-section above the fold owns
     the highlight. Shared by every admin settings page (S7). */
  const sectionIndex = () => document.querySelector(".bo-section-index");
  let spyTick = false;
  function syncScrollSpy() {
    const idx = sectionIndex();
    if (!idx) return;
    const sections = [...document.querySelectorAll(".bo-section[id]")];
    if (!sections.length) return;
    const probe = window.scrollY + 140; // below the sticky topbar
    let current = sections[0];
    sections.forEach((s) => { if (s.offsetTop <= probe) current = s; });
    // Bottom of page: the last section may never reach the probe line.
    if (window.innerHeight + window.scrollY >= document.body.offsetHeight - 2)
      current = sections[sections.length - 1];
    idx.querySelectorAll("a").forEach((a) =>
      a.classList.toggle("active", a.getAttribute("href") === `#${current.id}`));
  }
  window.addEventListener("scroll", () => {
    if (spyTick || !sectionIndex()) return;
    spyTick = true;
    requestAnimationFrame(() => { spyTick = false; syncScrollSpy(); });
  }, { passive: true });

  /* ---------- Toolbar filter controls: submit their GET form on change ----------
     Selects and (B1) the details.rd-filter checkboxes opt in via data-autosubmit. */
  document.addEventListener("change", (e) => {
    const ctl = e.target.closest("[data-autosubmit]");
    if (ctl && ctl.form) ctl.form.submit();
  });

  /* ---------- Tabs: [data-tabs] > [data-tab=panelId]; panels .rd-tab-panel ---------- */
  document.addEventListener("click", (e) => {
    const tabBtn = e.target.closest("[data-tab]");
    if (tabBtn) {
      e.preventDefault();
      const container = tabBtn.closest("[data-tabs]");
      const scope = container?.getAttribute("data-tabs-scope");
      container?.querySelectorAll("[data-tab]").forEach((b) => b.classList.toggle("active", b === tabBtn));
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

    // B4 builder rule rows: [data-rule-add="templateId"] appends a clone of the
    // template before itself — or into [data-rule-into="selector"] when the button
    // sits outside the row container (settings-tickets dlg-seq table rows, S7);
    // the row's .remove button deletes it (agent tickets advanced search; admin
    // builders reuse the same contract in S7).
    const ruleAdd = e.target.closest("[data-rule-add]");
    if (ruleAdd) {
      e.preventDefault();
      const tpl = document.getElementById(ruleAdd.getAttribute("data-rule-add"));
      if (tpl) {
        const into = ruleAdd.getAttribute("data-rule-into");
        const target = into ? document.querySelector(into) : null;
        if (target) target.append(tpl.content.cloneNode(true));
        else ruleAdd.before(tpl.content.cloneNode(true));
      }
    }
    // Roster/rule row removal: .bo-rule-row .remove (teams dialog) or the table-row
    // variant [data-roster-remove] (admin/department-edit member rows are <tr>s).
    const ruleRemove = e.target.closest(".bo-rule-row .remove, [data-roster-remove]");
    if (ruleRemove) {
      e.preventDefault();
      const row = ruleRemove.closest(".bo-rule-row, [data-roster-staff]");
      // Roster rows (admin/teams + department-edit): restore the removed member's option.
      const staffId = row?.getAttribute("data-roster-staff");
      if (staffId) {
        const opt = row.closest("[data-roster]")
          ?.querySelector(`select[data-roster-select] option[value="${CSS.escape(staffId)}"]`);
        if (opt) { opt.hidden = false; opt.disabled = false; }
      }
      row?.remove();
    }

    // Hierarchy collapse (admin/departments, B1 + ROADMAP "hierarchy (collapse)"):
    // a parent row's caret hides/shows every descendant row — rows carry their
    // materialized path in data-tree-path, the caret carries the parent's path.
    const treeToggle = e.target.closest("[data-tree-toggle]");
    if (treeToggle) {
      e.preventDefault();
      const path = treeToggle.getAttribute("data-tree-toggle");
      const collapsed = treeToggle.classList.toggle("collapsed");
      treeToggle.textContent = collapsed ? "▸" : "▾";
      document.querySelectorAll("[data-tree-path]").forEach((row) => {
        const p = row.getAttribute("data-tree-path");
        if (p !== path && p.startsWith(path)) {
          row.hidden = collapsed;
          if (!collapsed) {
            // Expanding resets nested carets so rows and toggles stay in sync.
            const nested = row.querySelector("[data-tree-toggle]");
            if (nested) { nested.classList.remove("collapsed"); nested.textContent = "▾"; }
          }
        }
      });
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

  /* ---------- Attach inputs list chosen files in the sibling .rc-file-name (B5) ---------- */
  document.addEventListener("change", (e) => {
    const input = e.target.closest('input[type="file"]');
    if (!input) return;
    const scope = input.closest("form") || input.closest(".rc-form-footer")?.parentElement || document;
    const label = scope.querySelector(".rc-file-name");
    if (label) label.textContent = [...input.files].map((f) => f.name).join(", ");
  });

  /* ---------- Select-all checkboxes: <input data-check-all="scopeSelector"> ---------- */
  document.addEventListener("change", (e) => {
    if (e.target.matches("[data-check-all]")) {
      const scope = document.querySelector(e.target.getAttribute("data-check-all")) || document;
      scope.querySelectorAll('tbody input[type="checkbox"]:not(:disabled)')
        .forEach((c) => (c.checked = e.target.checked));
    }
  });

  /* ---------- Multi-select filters (details.rd-filter) + chips row ---------- */
  document.addEventListener("click", (e) => {
    document.querySelectorAll("details.rd-filter[open]").forEach((d) => {
      if (!d.contains(e.target)) d.removeAttribute("open");
    });
    const clear = e.target.closest("[data-filter-clear]");
    if (clear) {
      e.preventDefault();
      document.querySelectorAll("details.rd-filter input:checked").forEach((c) => (c.checked = false));
      syncFilters();
      autoSubmitFilters();
    }
  });
  document.addEventListener("change", (e) => {
    if (e.target.matches('details.rd-filter input[type="checkbox"]')) syncFilters();
  });

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
      // Labels arrive pre-localized from the server; copy text instead of the
      // mockups' data-i18n lookup.
      const srcLabel = c.closest("label")?.querySelector("[data-i18n], span");
      if (!srcLabel) return;
      const chip = document.createElement("span");
      chip.className = "rd-chip";
      const label = document.createElement("span");
      label.textContent = srcLabel.textContent;
      chip.append(label);
      const x = document.createElement("button");
      x.type = "button";
      x.textContent = "✕";
      x.addEventListener("click", () => { c.checked = false; syncFilters(); autoSubmitFilters(); });
      chip.append(x);
      chipsWrap.insertBefore(chip, clearLink);
    });
    chipsWrap.hidden = checked.length === 0;
  }

  /* Server-driven filters (B1): when the filter checkboxes opt into data-autosubmit,
     chip removal / clear-all must round-trip too. */
  function autoSubmitFilters() {
    const inp = document.querySelector("details.rd-filter input[data-autosubmit]");
    if (inp && inp.form) inp.form.submit();
  }

  /* ---------- B10 "Yazdır": [data-print] triggers the browser print dialog ----------
     Page-specific print styling lives in app.css @media print. */
  document.addEventListener("click", (e) => {
    if (e.target.closest("[data-print]")) {
      e.preventDefault();
      window.print();
    }
  });

  /* ---------- B5 canned-response insert (agent ticket-view composer) ----------
     <select data-canned-insert="url"> fetches `url&what=<value>` → {text} and inserts
     it at the caret of the same form's textarea, then resets to the placeholder. */
  document.addEventListener("change", async (e) => {
    const sel = e.target.closest("select[data-canned-insert]");
    if (!sel || !sel.value) return;
    const what = sel.value;
    sel.selectedIndex = 0;
    const ta = sel.closest("form")?.querySelector("textarea");
    if (!ta) return;
    try {
      const res = await fetch(`${sel.getAttribute("data-canned-insert")}&what=${encodeURIComponent(what)}`);
      if (!res.ok) throw new Error();
      const data = await res.json();
      edInsert(ta, data.text || "", "", "");
    } catch {
      toast(sel.getAttribute("data-canned-err") || "!", "error");
    }
  });

  /* ---------- B5 signature preview: radios carry data-sig-text ----------
     The checked radio's text renders into the form's [data-sig-preview] block. */
  document.addEventListener("change", (e) => {
    const radio = e.target.closest("input[data-sig-text]");
    if (!radio) return;
    const preview = radio.closest("form")?.querySelector("[data-sig-preview]");
    if (!preview) return;
    const text = radio.getAttribute("data-sig-text") || "";
    preview.textContent = text;
    preview.hidden = text === "";
  });

  /* ---------- Advanced-search "save as queue" (agent tickets, B2) ----------
     <button data-advsearch-save="url"> posts its form's rule rows / columns / sort
     plus the queue name, then navigates to the created queue. The antiforgery token
     is borrowed from the page's POST form (#tq-bulk) so the GET search form stays
     token-free. */
  document.addEventListener("click", async (e) => {
    const btn = e.target.closest("[data-advsearch-save]");
    if (!btn) return;
    e.preventDefault();
    const form = btn.closest("form");
    if (!form) return;
    const fd = new FormData(form);
    const name = (fd.get("qname") || "").toString().trim();
    if (!name) {
      toast(btn.getAttribute("data-err-name") || "!", "error");
      return;
    }
    const token = document.querySelector('form[method="post"] input[name="__RequestVerificationToken"]');
    if (token) fd.append("__RequestVerificationToken", token.value);
    try {
      const res = await fetch(btn.getAttribute("data-advsearch-save"), { method: "POST", body: fd });
      if (!res.ok) throw new Error();
      const data = await res.json();
      location.href = data.url;
    } catch {
      toast(btn.getAttribute("data-err") || "!", "error");
    }
  });

  /* ---------- B9 topbar global search: debounce + fetch + fill the dropdown ----------
     <input data-global-search="/agent/search"> + sibling .bo-search-results panel.
     Chosen over a results page: the mockups define no search-results UI, so a
     dropdown fragment keeps the invention minimal (flagged for canon sign-off). */
  let searchTimer;
  let searchSeq = 0;
  document.addEventListener("input", (e) => {
    const inp = e.target.closest("input[data-global-search]");
    if (!inp) return;
    const panel = inp.parentElement.querySelector(".bo-search-results");
    if (!panel) return;
    clearTimeout(searchTimer);
    const q = inp.value.trim();
    if (q.length < 2) {
      panel.hidden = true;
      panel.innerHTML = "";
      return;
    }
    searchTimer = setTimeout(async () => {
      const seq = ++searchSeq;
      try {
        const res = await fetch(`${inp.getAttribute("data-global-search")}?q=${encodeURIComponent(q)}`);
        if (!res.ok || seq !== searchSeq) return; // stale or failed — keep current panel
        panel.innerHTML = await res.text();
        panel.hidden = false;
      } catch { /* network error: leave the panel as-is */ }
    }, 250);
  });
  document.addEventListener("click", (e) => {
    document.querySelectorAll(".bo-search-results:not([hidden])").forEach((p) => {
      if (!p.closest(".bo-search").contains(e.target)) p.hidden = true;
    });
  });
  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape")
      document.querySelectorAll(".bo-search-results").forEach((p) => (p.hidden = true));
  });

  /* ---------- Password strength + match feedback (B3: register/pwreset) ----------
     Inputs opt in via data attributes carrying server-localized messages:
       <input data-pw-strength="msg">                      — ≥8 chars, letters + digits
       <input data-pw-match="#otherId" data-pw-match-msg="msg"> — must equal the target
     Native constraint validation (setCustomValidity) blocks submit and surfaces
     the message; the server repeats the same checks (VM annotations). */
  function syncPasswordChecks(form) {
    form.querySelectorAll("input[data-pw-strength]").forEach((inp) => {
      const weak = inp.value !== "" && !/^(?=.*\p{L})(?=.*\d).{8,}$/u.test(inp.value);
      inp.setCustomValidity(weak ? inp.getAttribute("data-pw-strength") : "");
    });
    form.querySelectorAll("input[data-pw-match]").forEach((inp) => {
      const target = document.querySelector(inp.getAttribute("data-pw-match"));
      const mismatch = inp.value !== "" && target && inp.value !== target.value;
      inp.setCustomValidity(mismatch ? inp.getAttribute("data-pw-match-msg") || "" : "");
    });
  }
  document.addEventListener("input", (e) => {
    if (e.target.matches("input[data-pw-strength],input[data-pw-match]") && e.target.form)
      syncPasswordChecks(e.target.form);
    else if (e.target.matches("input[type=password]") && e.target.form?.querySelector("[data-pw-match]"))
      syncPasswordChecks(e.target.form); // primary field changed → recheck the confirm
  });

  /* ---------- Rich-text editor (attached to every textarea) ---------- */
  // Toolbar titles/placeholders are read from data-editor-* attributes the
  // layout stamps on <body> (server-localized), falling back to TR defaults.

  const ED = (key, fallback) => document.body.getAttribute(`data-editor-${key}`) || fallback;

  const EDITOR_TOOLS = [
    { cmd: "bold", label: "<b>B</b>" },
    { cmd: "italic", label: "<i>I</i>" },
    { cmd: "underline", label: "<u>U</u>" },
    { cmd: "strike", label: "<s>S</s>" },
    { sep: true },
    { cmd: "ul", label: "☷" },
    { cmd: "ol", label: "1." },
    { sep: true },
    { cmd: "link", label: "🔗" },
    { cmd: "photo", label: "🖼" },
    { cmd: "video", label: "🎬" },
    { cmd: "file", label: "📎" },
    { sep: true },
    { cmd: "table", label: "▦" },
    { cmd: "hr", label: "―" },
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
        <select data-ed="font" title="${ED("font", "Yazı tipi")}">
          <option value="">Inter</option>
          <option value="Arial">Arial</option>
          <option value="Georgia">Georgia</option>
          <option value="'Courier New',monospace">Courier New</option>
        </select>
        <select data-ed="size" title="${ED("size", "Yazı boyutu")}">
          <option value="12px">12</option>
          <option value="14px" selected>14</option>
          <option value="16px">16</option>
          <option value="18px">18</option>
        </select>
        <span class="rd-editor-separator"></span>
        ${EDITOR_TOOLS.map((tool) => tool.sep
          ? '<span class="rd-editor-separator"></span>'
          : `<button type="button" class="rd-editor-tool" data-ed="${tool.cmd}">${tool.label}</button>`
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
    switch (ctl.getAttribute("data-ed")) {
      case "font": ta.style.fontFamily = ctl.value; break;
      case "size": ta.style.fontSize = ctl.value; break;
      case "bold": edInsert(ta, "**", "**", ED("ph-text", "metin")); break;
      case "italic": edInsert(ta, "*", "*", ED("ph-text", "metin")); break;
      case "underline": edInsert(ta, "__", "__", ED("ph-text", "metin")); break;
      case "strike": edInsert(ta, "~~", "~~", ED("ph-text", "metin")); break;
      case "ul": edInsert(ta, "\n• ", "", ED("ph-item", "madde")); break;
      case "ol": edInsert(ta, "\n1. ", "", ED("ph-item", "madde")); break;
      case "link": edInsert(ta, "[", "](https://)", ED("ph-link", "bağlantı")); break;
      case "hr": edInsert(ta, "\n――――――――――\n", "", ""); break;
      case "table":
        edInsert(ta, `\n| ${ED("ph-col", "Sütun")} 1 | ${ED("ph-col", "Sütun")} 2 |\n| --- | --- |\n|  |  |\n`, "", "");
        break;
      case "photo": case "video": case "file":
        toast(ED("mock-picker", "Dosya seçici bu aşamada temsilidir."));
        break;
    }
  }

  /* ---------- B4 drag-reorder: [data-drag-rows] + [data-drag-handle] ----------
     The mockups promise ⋮⋮-handle reordering (queues.html columnsHelp) but define
     no JS contract — INVENTED minimal HTML5 DnD, shared so the S7 form/list
     builders reuse it: a container opts in via data-drag-rows; pressing a child
     row's [data-drag-handle] arms the row, dragging over siblings reorders it in
     place. The DOM order IS the persisted order (rows post as parallel arrays). */
  let dragRow = null;
  document.addEventListener("mousedown", (e) => {
    const handle = e.target.closest("[data-drag-handle]");
    const row = handle?.closest("tr, .bo-rule-row, li");
    if (row && row.parentElement?.hasAttribute("data-drag-rows")) row.draggable = true;
  });
  document.addEventListener("dragstart", (e) => {
    const row = e.target.closest?.("[draggable=true]");
    if (!row || !row.parentElement?.hasAttribute("data-drag-rows")) return;
    dragRow = row;
    e.dataTransfer.effectAllowed = "move";
    try { e.dataTransfer.setData("text/plain", ""); } catch { /* IE-era quirk guard */ }
  });
  document.addEventListener("dragover", (e) => {
    if (!dragRow) return;
    const over = e.target.closest("[data-drag-rows] > *");
    if (!over || over === dragRow || over.parentElement !== dragRow.parentElement) return;
    e.preventDefault();
    const rect = over.getBoundingClientRect();
    over.parentElement.insertBefore(
      dragRow, e.clientY < rect.top + rect.height / 2 ? over : over.nextSibling);
  });
  document.addEventListener("drop", (e) => { if (dragRow) e.preventDefault(); });
  document.addEventListener("dragend", () => {
    if (dragRow) { dragRow.draggable = false; dragRow = null; }
  });

  /* ---------- Toast ---------- */

  function toast(message, variant) {
    let el = document.getElementById("rd-toast");
    if (!el) {
      el = document.createElement("div");
      el.id = "rd-toast";
      el.style.cssText =
        "position:fixed;bottom:24px;left:50%;transform:translateX(-50%);" +
        "background:var(--rd-petrol);color:#fff;padding:11px 19px;border-radius:11px;" +
        "font-size:13px;z-index:99;box-shadow:0 10px 30px rgba(0,0,0,.25);transition:opacity .3s";
      el.setAttribute("role", "status");
      el.setAttribute("aria-live", "polite");
      document.body.appendChild(el);
    }
    el.style.background = variant === "error" ? "var(--rd-danger, #b3261e)" : "var(--rd-petrol)";
    el.textContent = message;
    el.style.opacity = "1";
    clearTimeout(el._hide);
    el._hide = setTimeout(() => (el.style.opacity = "0"), 2200);
  }
  window.RD = Object.assign(window.RD || {}, { toast });

  /* ---------- Boot ---------- */

  function boot() {
    enhanceTextareas();
    syncFilters();
    syncGates();
    syncMasters();
    syncScrollSpy();
    // Server-rendered toast handoff: <body data-toast="..."> after a redirect.
    const pending = document.body.getAttribute("data-toast");
    if (pending) toast(pending, document.body.getAttribute("data-toast-variant") || undefined);
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", boot);
  else boot();
})();
