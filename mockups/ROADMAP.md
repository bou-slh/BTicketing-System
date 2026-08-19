# RapidsolDestek — Product Roadmap

Timeline-free, dependency-ordered roadmap for building the real RapidsolDestek helpdesk in C#/ASP.NET Core.
The mockups in this folder (72 TR/EN pages: portal 12, agent 18, admin 42 — incl. the staff pwreset pages added in S0) are the **binding spec**: the shipped
product must look and behave exactly like them, and **every component visible in a mockup must work** — including
every control that is currently dead in the static HTML. osTicket (`/Users/rapidsolbilisim/os/osTicket`) is the
domain-model reference; §3 tracks feature parity with it subsystem by subsystem. No osTicket code is reused (GPLv2).

---

## 1. Charter & definition of done

- **Stack**: .NET 10 · ASP.NET Core MVC with Razor areas `Portal` / `Agent` / `Admin` · EF Core + PostgreSQL ·
  Hangfire (background jobs) · SignalR (live board) · MailKit/MimeKit (email) · xUnit + Playwright.
- **Spec rule**: mockups are spec v1.0 after Stage S0 freezes them. Deviations require sign-off.
- **Definition of done — per page**: its §6 parity checklist is fully green. Every listed component functions,
  in TR **and** EN, in light **and** dark theme, with the page's empty state reachable. "It renders correctly"
  is never done.
- **Definition of done — per stage**: the stage's exit gate (§5) passes, `/verify` (build + tests + i18n audit +
  Playwright smoke) is green, and the visual differ accepts the stage's pages against their mockup twins.
- **Tenancy**: **single-tenant** (decided 2026-08-13) — one deployment, one company's helpdesk; no tenant isolation in the schema.
- **Scope**: full scope, single launch. The admin **builders** (queue builder, form designer, list editor,
  filter rule editor) are **in v1** — no deferred-to-v2 components anywhere.

## 2. Canonical domain data

Single source of truth for sample/seed data. **All of it is illustrative sample content** — it exists so pages read coherently and the dev/demo database has consistent fixtures; real business data replaces it at launch and nothing in the product may hardcode these values. It serves twice: (a) the S0 mockup consistency pass edits every
mockup page to conform, (b) S3 seeds the database with exactly this canon. Authority pages win conflicts.

| Domain | Canon (authority page) | Currently conflicting pages to conform |
|---|---|---|
| Departments | **Destek**, **Bordro** (child of Destek), **Danışmanlık** (`admin/departments.html`) | `admin/settings-system.html` ("İK Danışmanlık"), `admin/helptopics.html` + `helptopic-edit.html` ("Bordro Yönetimi", "İnsan Kaynakları"), `admin/dashboard.html` stats (5 invented depts), `admin/department-edit.html` parent select |
| Agents | **Ümit Yaşar Akın (uakin·Bordro), Merve Çetin (mcetin·Danışmanlık), Deniz Kaya (dkaya·Destek), Selin Aydın (saydin·Bordro), Kerem Yılmaz (kyilmaz·Destek), Aslı Doğan (adogan·Destek)** (`agent/directory.html` roster); full name in headers/directory, "Ümit Y. Akın" in table cells | "Ümit Akın" spellings (~8 files, fixed S0); `admin/staff.html` phantom "zarslan" → saydin (fixed S0) |
| SLA plans | **Standart (48h grace), VIP, Kritik, Dahili Talepler (72h)** (`admin/slas.html`) | `admin/department-edit.html` ("Standart — 8 saat"), `admin/settings-tickets.html` ("Standart — 4 saat", invented "Bordro Dönemi") |
| Schedules | **Hafta içi 09:00–18:00**, **7/24**, **Resmi Tatiller 2026**, **Cumartesi Yarım Gün** (`admin/schedules.html`) | spelling variants in `admin/department-edit.html`, `admin/settings-system.html` |
| Statuses | Global enum in `assets/js/i18n-tr.js`: `open, wait, test, solved, closed, overdue, new, effortWait, effortApproved, effortRejected` | portal-local inventions `replyWait`, `testing`, `inProgress` (`portal/index.html`, `portal/tickets.html`) map onto the global enum |
| Hero end-user | **Bourla Salehi · bourla.salehi@ulasim.com.tr · org Ulaşım A.Ş.** (the hero-ticket story wins: R716555 is an Ulaşım ticket and Bourla approves its effort on the portal) | `agent/users.html`/`user-view.html` + `app.js` account block (were RapidSol), `portal/profile.html` (b.salehi@) — all conformed in S0 |
| Portal ownership | A portal user sees only their own org's tickets | `portal/tickets.html` currently mixes 4 companies for one user |
| Ticket numbering | One scheme, `R` + 6 digits (R7165xx family) across all panels | portal-only R794094/R11xxxx numbers renumbered into the family |
| Hero ticket | **R716555** "Yol ücreti hatası hakkında" (Ulaşım A.Ş., 6h effort pending) — already consistent across 7 pages; stays the golden-path story | keep; align its status (`open` + pending effort) on `agent/dashboard.html` (`effortWait`) and `agent/user-view.html` (`wait`) |
| Companies | Ulaşım A.Ş., Tosyalı Holding, Konecta, RapidSol (consistent today) | counts on `agent/orgs.html` vs visible rows reconciled |
| Emails | destek@ / bordro@ / bilgi@rapidsol.com.tr (consistent today) | — |
| Templates | Sets: Varsayılan (TR), English Set (EN) (consistent today) | — |
| Cross-page integrity | Assignees, statuses, org ticket lists, task links, tile counts, relative dates agree between every pair of pages that show the same entity | audited conflicts C1–C14: tickets↔live assignees, tickets↔users rosters, org-view ticket lists, ticket-view↔tasks task mismatch, sidebar/tile counts, "Bugün/Dün" date logic |

## 3. osTicket parity matrix

Every osTicket subsystem (from `include/class.*.php` + `scp/*.php`) appears below — nothing is silently missing.
Levels: **full** (equivalent capability), **adapted** (capability kept, mechanism modernized), **replaced**
(different mechanism, same job), **dropped** (deliberately out, stated reason).

| osTicket subsystem | RapidsolDestek module | Mockup page(s) | Parity |
|---|---|---|---|
| ticket, thread, thread_actions | TicketService + ThreadEntry (reply/note/event) | agent/tickets, ticket-view, ticket-open; portal/tickets, ticket-view, open | full |
| collaborator | Ticket collaborators (CC) | CC field on agent/ticket-open, recipients row on ticket-view | full |
| lock | Concurrent-edit lock on composers/edit forms | (implied by composers) | full |
| draft | Autosaved composer drafts | (implied by composers) | full |
| task | TaskItem + task threads | agent/tasks, task-view; admin/settings-tasks | full |
| user, organization | User + Organization (+ domain auto-link, field inheritance/override) | agent/users, user-view, orgs, org-view; admin/settings-users | full |
| staff, team, role, dept | Staff + Team + Role(permission matrix) + Department | admin/staff(-edit), teams, roles(role-edit), departments(-edit); agent/directory | full |
| topic (help topics) | HelpTopic (routing: dept/priority/SLA/form) | admin/helptopics(-edit) | full |
| sla | SlaPlan | admin/slas | full |
| schedule, businesshours | Schedule + holiday entries + diagnostic | admin/schedules, schedule-edit | full |
| filter, filter_action | Mail/ticket filters with actions | admin/filters, filter-edit | full (builder in v1) |
| queue (custom queues) | SavedQueue + queue builder (criteria/columns/sort/preview) | admin/queues; agent/tickets queue tree + advanced search | full (builder in v1) |
| dynamic_forms, forms | Form designer (custom fields on ticket/task/user/org) | admin/forms, form-edit | full (builder in v1) |
| list (custom lists) | List editor (+ system lists protected) | admin/lists, list-edit | full (builder in v1) |
| faq, category, knowledgebase | KB: categories, articles, portal search, helpful votes | portal/kb, kb-article; agent/kb, kb-faq; admin/settings-kb | full |
| canned | Canned responses + variable expansion + composer insert | agent/canned; selects in ticket-view/ticket-open | full |
| email, mailfetch, mailer | EmailAccount (IMAP/SMTP + OAuth2), inbound pipeline, outbound queue | admin/emails, email-edit, email-settings | full |
| template (email templates) | EmailTemplate sets, per-template editor, variable pills | admin/templates, template-edit | full |
| banlist | Banlist | admin/banlist | full |
| emailtest | Email diagnostic (real send, pending/success/failure states) | admin/email-diagnostic | full |
| config/settings | Typed Setting sections consumed by the engine | admin/settings-* (7 pages) | full |
| log, audit | System logs + AuditEvent on every mutation (EF interceptor) | admin/system-logs, audit-logs | full |
| api, apikeys, dispatcher | REST API + ApiKey (create/regenerate/copy, IP restriction) | admin/apikeys | adapted (JSON REST, no XML) |
| plugin, app | Feature flags + integration toggles | admin/plugins | replaced (no PHP-style plugin runtime; the page manages built-in feature modules) |
| 2fa | TOTP 2FA (mandatory for admins) | 2FA blocks on all three logins | full |
| auth, oauth2, passwd, usersession | ASP.NET Core Identity: Customer + Staff principals, lockout, pwreset, sessions | portal/login, register, pwreset; agent/login; admin/login | adapted |
| search | Postgres full-text (`tsvector`) topbar + list search | topbar search, list search boxes | replaced |
| export, pdf | CSV export on lists + print views (browser print CSS) | "Dışa Aktar" buttons, "Yazdır" on ticket-view | adapted |
| cron/autocron | Hangfire recurring jobs (mail fetch, SLA/overdue sweep, retention) | (invisible; drives live board + overdue statuses) | replaced |
| i18n, translation | resx + culture middleware, TR default, EN cookie switcher | TR/EN switch on every page | adapted |
| sequence | Ticket/task numbering sequences | admin/settings-tickets `dlg-seq`, settings-tasks | full |
| import | CSV import for users | "İçe Aktar" on agent/users; `dlg-import` on admin/list-edit | full |
| captcha | Rate limiting + optional captcha on guest/register endpoints | (invisible; portal/register, check-status) | adapted |
| avatar | Initials avatars (local generation, per mockup style) | avatars throughout | adapted |
| company (site pages) | SitePage (landing/offline/thanks) + company settings | admin/pages, settings-company | full |
| pagenate | Server-side pagination on every list | all `.rd-pagination` bars | full |
| **(no osTicket counterpart)** | **EffortProposalService** — Efor Onayı state machine `none→pending→approved/rejected→(revise→pending)` | effort card portal/ticket-view; effort dialog + banners agent/ticket-view; settings-tickets effort section; 2 email templates | product-original |
| **(no osTicket counterpart)** | **Live board** — SignalR real-time kanban + ticker + SLA countdown | agent/live | product-original |

Deliberately dropped: osTicket's PHP plugin marketplace, XML API payloads, osTicket's attachment-in-database
default (files go behind `IFileStore`, filesystem/S3-compatible).

## 4. Cross-cutting component behavior specs

Written once here; every page checklist in §6 references them. Each spec exists because the audit found the
pattern dead in the mockups.

- **B1 List engine** (~19 admin + 8 agent/portal list pages): server-driven sort on every `th.sortable`
  (direction indicator moves), live search box, filter dropdowns as the existing `details.rd-filter` chip
  component (adopt it on admin lists — it is currently used on exactly one page), real pagination,
  row-checkbox selection with header select-all (incl. reverse sync + `:disabled` rows skipped), a
  "N seçildi" bulk bar whose actions (Ata/Birleştir/Sil/Etkinleştir/…) actually operate on the selection,
  CSV export, and a reachable empty state (currently unreachable hidden panels on 16 admin pages).
- **B2 Dialogs**: every dialog is parameterized by the row that opened it (today one hardcoded dialog is
  shared by 21 template rows, 6 user-template rows, 4 SLA rows, 4 site pages, 4 API keys, 3 ban rows,
  3 teams, all field-config rows). Submit = validate → save → close → toast, fixing the audited pattern
  where `data-dialog-close` on a submit button swallows the save. Backdrop click + ESC close, focus trap,
  focus return.
- **B3 Forms & gating**: server + client validation, dirty-state guard, password-match checks; a
  `.rd-switch` gates its dependent fields (disable/reveal) — ~140 admin switches incl. the Efor Onayı
  section, alert `bo-toggle-card` masters, email-edit protocol radios, KB master switch; permission
  matrices (role-edit 42 boxes, staff-edit 39) get master↔children with indeterminate state; auth-backend
  and format selects reveal their dependent inputs.
- **B4 Builders** (v1): repeatable rows — every "＋ Add …" (~18) appends a row, every `✕` (~45) removes one;
  drag-reorder via the `⋮⋮` handles (queue columns, form fields, list items — mockup help text already
  promises it); queue builder's Preview tab reflects the configured criteria/columns/sort live; filter
  editor shows a "N tickets would match" preview.
- **B5 Composers**: replies/notes append to the thread (agent/ticket-view, task-view, user-view, org-view,
  portal/ticket-view); canned-response select inserts expanded text into the editor; template variable
  pills are click-to-insert at cursor; attach controls list chosen files (the empty `.rc-file-name` span
  and the input-less `.rd-upload` drop zone get wired); signature radios preview; after-reply status select
  applies.
- **B6 Auth & session**: real `<form>` logins (all three logins are currently `<a>` navigations that ignore
  credentials), TOTP 2FA (mandatory for admin), lockout policy, logout in the account menu (no logout
  exists anywhere in the mockups today), staff pwreset flow (osTicket has `scp/pwreset.php`; mockups lack
  agent/admin pwreset pages — S0 adds them), portal register/pwreset as real multi-step flows (pwreset
  mockup currently shows all 3 steps stacked), remember-me, invalid-credential error states.
- **B7 Live board**: SignalR hub pushes column membership, ticker events and SLA countdowns from domain
  events; cards drag between the 5 columns with permission checks; "Üstlen" claims and moves the card;
  SLA chips can leave warn/danger when rescued; pause stops updates visibly.
- **B8 Effort loop (flagship)**: agent proposes/revises/withdraws (dialog exists) → portal user sees the
  effort card → **Onayla** (one click) or **Reddet** (dialog, mandatory note) → status transition + thread
  event + banner variant on both panels (approved/rejected banner variants already exist in the mockup as
  HTML comments) → emails via the two effort templates → settings gates (enable, block-work-until-approved,
  mandatory reject note, reminder days, auto-approve threshold, revision limit).
- **B9 Shell**: topbar global search (searches tickets/users/orgs/settings, currently decorative), account
  menu on the avatar (profile + logout), sidebar counts fed by real queries (hardcoded "8"/"3" today),
  settings section rail with scroll-spy (click-highlight exists, scroll desyncs today), panel switcher
  respecting permissions.
- **B10 Observability & polish**: audit trail on every mutation with drill-down from audit-logs rows;
  data-driven dashboard charts with hover tooltips (admin SVG chart is hardcoded; date-range apply must
  re-render); email diagnostic with pending/success/failure states (success banner is currently always
  visible); schedule diagnostic answers its date question; "Yazdır" = print CSS; toasts with
  success/error variants; a11y (dialog focus, tab roles/aria-selected, aria-live on toasts/ticker);
  dark/light via `light-dark()` kept working in ported CSS.

## 5. Stages S0–S9 (dependency order, exit gates, no dates)

- **S0 — Spec freeze & mockup consistency pass.** Apply §2 canon to every mockup page's sample data; fix
  mockup defects that make the spec lie: dead tab bars on `agent/tasks.html` + `agent/kb.html` (missing
  `data-tab`/panels), decoy language selects on both profile pages (wire to `data-lang-switch`), orphaned
  `portal/profile.html` + `portal/check-status.html` (link from portal header), missing file input in
  `agent/kb-faq.html` upload zone, `admin/email-diagnostic.html` always-on success banner, add agent/admin
  pwreset mockups, normalize `data-check-all` selector convention. Tag `spec-v1.0`.
  *Gate*: cross-page grep/diff pass shows zero canon violations; gallery links resolve; PM sign-off.
- **S1 — Bootstrap** *(starts today)*. Git repo at `RapidsolDestek/` (mockups tracked), solution skeleton
  (`src/Web` with 3 areas, `src/Domain`, `src/Infrastructure`, `tests/Tests`, `tests/E2E`), Docker compose
  (app + PostgreSQL), CI (build+test on PR), CLAUDE.md; AI tooling: `page-porter` agent, `/port-page`,
  `/add-entity`, `/add-setting`, `/verify` skills, format/i18n hooks.
  *Gate*: `dotnet build` + `docker compose config` green; `/port-page` demo runs end-to-end on a throwaway branch.
- **S2 — Chrome, i18n, auth foundation.** `_PortalLayout` + `_BackofficeLayout` replicating the injected
  chrome (nav highlighting from routes); i18n-js→resx converter + culture middleware (TR default, EN cookie);
  Identity with separate Customer/Staff principals and cookies; B6 complete (2FA, lockout, logout, pwreset).
  *Gate*: all 6 auth pages functional; chrome pixel-matches mockups in TR/EN, light/dark.
- **S3 — Domain model + seed.** Entity sweep using §3 as the checklist (Ticket, ThreadEntry, TaskItem, User,
  Organization, Staff, Team, Role, Department, HelpTopic, SlaPlan, Schedule, Filter+Actions, SavedQueue,
  FormDefinition/Field, ListDefinition/Item, KbCategory, FaqArticle, CannedResponse, EmailAccount,
  EmailTemplate, Attachment, AuditEvent, Setting, ApiKey, BanlistEntry, SitePage, EffortProposal(+revisions),
  Sequence); EF interceptor writing AuditEvents; seed = §2 canon exactly.
  *Gate*: schema review vs osTicket reference; seeded R716555 renders in a real-chrome queue.
- **S4 — Core services.** TicketService (create/route/transition with permission checks), thread with
  collaborators/drafts/locks/sanitization, assignment+claim, queue engine (SavedQueue eval + `tsvector`
  search), canned+variables, **EffortProposalService** (B8 state machine + guards + domain events),
  `IFileStore`. *Gate*: service test suite green incl. full effort lifecycle and revision loops.
- **S5 — Portal area** (12 pages, checklists §6.1). *Gate*: portal E2E — register → open → reply → effort
  approve + reject paths; visual diff accepted. *(Differ #3 2026-08-14: login/register/pwreset×3/offline/
  profile all PASS in 4 states — S5 visual diff accepted. Gate E2E green 2026-08-14, `PortalGoldenPathTests`:
  fresh-user register → open → reply incl. org auto-link assert; hero-proposal Reddet mandatory-note guard +
  Onayla decision. The full portal reject transition is service-tested (S4 suite) and gets its E2E in the S6
  golden path — the hero proposal is the only seeded pending one and a decided proposal can't be re-proposed
  until agent-side revise exists. Gate also surfaced+fixed: registration created no domain User row, so fresh
  registrants couldn't open tickets; now claims an existing unlinked user by email or creates one with
  Organization.Domain auto-link.)*
- **S6 — Agent area** (18 pages, §6.2; B1 on lists, B5 composers, B7 live board). *Gate*: golden path E2E
  (portal open → agent proposes 6h → portal approves → agent resolves); live board updates <1s across two
  browsers. *(Golden-path leg GREEN 2026-08-15: `GoldenPathTests` — fresh portal user opens a Bordro ticket,
  saydin proposes 6h via dlg-effort, portal Onayla flips the card, agent replies + resolves to Çözüldü,
  both panels reflect it. Live-board leg GREEN 2026-08-18: `LiveBoardTests` — two browser contexts
  (dkaya + kyilmaz) sit on /agent/live over LiveBoardHub; dkaya's Üstlen appears on kyilmaz's board in
  <1 s (1 s-timeout web-first assert plus a stopwatch guard), then both boards converge the card out of
  the claimable columns. Fresh-seed dependent like the rest of the suite. STAGE CLOSED 2026-08-18: all 18 §6.2 pages ported, both gate legs green, visual-differ pass over the final five pages (kb, kb-faq, canned, profile, live) ×TR/EN×light/dark incl. dialogs — one S1 caught+fixed (cn.fBodyHelp unescaped braces, see canned row) — all 5 at parity.)*
- **S7 — Admin area** (42 pages, §6.3; B1–B4 everywhere, builders, settings actually consumed by the engine).
  *Gate*: every setting round-trips (flip block-work-until-approved → S4 guard flips); builder-created
  queue/form/list/filter demonstrably affects the agent panel. *(Leg 1 GREEN since the settings-tickets
  port (admin flip → S4 guard flips, SettingsTicketsTests). Leg 2 GREEN 2026-08-19 via the queue builder:
  E2E `QueueBuilderTests` — admin uakin (password + TOTP) builds "E2E Acil Bordro …" through the real
  /admin/queues UI (criteria rows dept=Bordro + priority=Acil under parent "Açık"; the live Preview tab is
  asserted to show R716536 and hide R716561 BEFORE saving), then agent saydin finds the queue in the
  tickets queue tree, opens it and the list shows exactly the matching R716536 — 5 s against a freshly
  seeded instance. Filters already run in the live create pipeline (filters row). Forms/lists builder legs
  GREEN 2026-08-19 (HTTP-level, FormsListsAdminTests GateProof): a form built through the real
  /admin/form-edit POST (required email field w/ ⚙ validation + inline-choices field) and attached to a
  fresh public topic through /admin/helptopic-edit renders on BOTH open pages (portal /open + agent
  /agent/ticket-open — labels, ⚙ hint, choice options), the ⚙ email validation refuses a malformed
  portal submit, and a valid submit persists the designer fields as FormEntry/Values on the created
  ticket; the list editor feeds those choice fields (form-edit list select = real ListDefinitions).)*
- **S8 — Email subsystem.** Outbound: Razor template rendering with variables, event→template map (incl.
  effort request/response), MailKit SMTP, Hangfire send queue with retry, per-department from-addresses.
  Inbound: Hangfire IMAP poll, MimeKit parse, reply-token threading, help-topic routing, attachments,
  banlist + loop/bounce protection. *Gate*: staging round trip — mail → ticket → agent reply → customer
  mail → customer reply → thread appends.
- **S9 — Hardening & launch.** Security review + authz matrix tests (every role × endpoint), rate limiting,
  CSRF/headers, dependency scan; KVKK (data inventory, retention jobs, consent texts); performance (index
  audit, slow-query profiling, load test lists+search); backups/monitoring/health checks; data migration
  (if a legacy source is confirmed) with dry-run reconciliation; UAT rounds; production cutover + hypercare.
  *Gate*: security review clean, UAT signed, backup/restore drill done, rollback documented.

Golden-path E2E runs in CI nightly from S6 onward.

## 6. Page-by-page parity checklists

Legend: each `[ ]` is a component that must work per the referenced B-spec. Working links/tabs/dialogs already
proven in the mockups are not repeated — the lists below name what is dead today plus the page's core dynamic
behavior. (Component inventory source: the three mockup audits, 2026-08-13.)

### 6.1 Portal (12 pages)

- [x] **login.html** — real form + validation, remember-me, error state, 2FA (B6) *(S5; Identity sign-in with
      lockout, invalid-credential + locked-out states via `.rd-form-error`; guest check-status link live; 2FA
      stays open — the mockup has no portal 2FA UI, deferred TODO(S7); open decision: portal /login is
      offline-blocked (customer route, staff enter via their own logins) — matches osTicket, confirm intended)*
- [x] **register.html** — real registration, password match/strength, consent (KVKK), multi-step per design (B6)
      *(S5; mockup is single-page with 3 numbered sections — followed exactly, not multi-step; client
      strength/match feedback in rd.js + server Identity errors localized; KVKK checkbox is NOT in the mockup —
      added per spec with stock `.rd-check`, mockup needs an S0-style pass to gain the row (visual-differ will
      flag); timezone select bound, persistence TODO(S7) with profile)*
- [x] **pwreset.html** — 3 steps as an actual flow (request → code → new password) (B6) *(S5; /pwreset →
      /pwreset/sent → /pwreset/new mail-token flow, each page renders its mockup step's DOM incl. "Adım N / 3"
      captions; enumeration-safe; parity note: Sent page keeps a "← Girişe dön" foot link the mockup's step-2
      card lacks — kept for usability, remove if strict parity wins)*
- [x] **offline.html** — served by real maintenance-mode switch (settings-system) *(S5; Setting
      namespace=system key=offline via ISettingsService, default off; middleware blocks client-portal routes
      only — staff/admin reachable, osTicket semantics; admin toggle TODO(S7); 24 unit tests)*
- [x] **index.html** — live tile counts, KB mini-search works, recent tickets from data, account menu + logout (B9) *(S5; recent rows deep-link `/ticket-view?id=` — dead until portal ticket-view ports; account menu + logout via shared portal header)*
- [x] **open.html** — help-topic → dynamic form fields (form designer output), file list on attach, validation, creates a real ticket (B3/B5) *(S5; topic select = public+active HelpTopics, seeded HelpTopicForm fields swap in client-side and persist as FormEntry/Values — S7 designer will edit the definitions; attachments via IFileStore onto the initial Message entry; priority select = real normal/high/emergency rows; reference field stored as a labelled line in the first message until the built-in ticket form exists (TODO S7); creates via ITicketService → redirects /tickets; differ note: new Yol Ücreti tickets number as BRD-7166xx — faithful to the topic's canon NumberFormat "BRD-######" (admin/helptopic-edit) but visually off-canon vs §2's single R-scheme; resolve when the numbering canon is revisited)*
- [x] **tickets.html** — real list: tabs from data, search + topic filter work, deep links `ticket-view?id=` (B1) *(S5; open/closed tabs + counts live incl. derived effortWait pills, search + public-topic filter compose with the active tab; row links `/ticket-view?id=` — dead until portal ticket-view ports)*
- [x] **ticket-view.html** — thread from data; **effort card Onayla/Reddet** (B8); reply composer appends + attach file list (B5) *(S5; thread renders Message/Response only — Notes stay internal; effort card drives EffortProposalService with the owner as decider, decided card shows canon effortApproved/effortRejected + decision note; reply POSTs via IThreadService + IFileStore attachments, chips download owner-gated; signed guest token from check-status opens a read-only view; history timeline from thread events — mockup's "Yönetici incelemesi tamamlandı" row has no portal-safe data source (internal note), left out)*
- [x] **check-status.html** — guest lookup (ticket# + email) renders a result/failure state *(S5; access-link mail via dev sender until S8; mail now carries the signed one-hour guest token → read-only /ticket-view)*
- [x] **kb.html** — search actually searches, category filter, article counts (B1) *(S5; two visual-differ notes:
      category headers are DB data so they stay TR in EN — mockup's kb.cat* localization needs a per-category
      translation decision; card subtitles fall back to the category description until articles carry a summary)*
- [x] **kb-article.html** — helpful yes/no vote with thank-you state; attachments downloadable *(S5; vote + thank-you live; attachments download via IFileStore — seeded canon files carry placeholder bytes)*
- [x] **profile.html** — saves; language select actually switches culture; password change validated (B3) *(S5;
      name/phone/timezone/language persist on CustomerUser (new TimeZone/Language columns, S5_CustomerPrefs
      migration) and name/phone sync to the domain User row + FullName claim/cookie; register.html's deferred
      timezone select now persists too, and login applies the saved language; language select keeps the mockup's
      data-lang-switch → submits the profile form, save sets the culture cookie server-side so the response is
      already in the new language; password change optional via ChangePasswordAsync with rd.js strength/match
      hints + localized Identity errors; success = rd.js toast via body[data-toast] handoff (mockup's submit
      toast); email readonly per mockup; NavKey "home" faithful to the mockup's data-nav; differ #3 PASS all
      4 states — fixed seed gap: identity PhoneNumber now seeded from this mockup's "+90 532 481 22 15", which
      CONFLICTS with agent/user-view.html's "+90 212 555 0180" for the same person — absorbed as identity
      phone vs domain User.Phone for now, needs a §2 canon decision)*
- [x] *(portal header everywhere)* — profile + check-status reachable, logout (B9) *(S5; header account menu →
      /profile now resolves on every portal page, guest check-status linked from login, logout POST live)*

### 6.2 Agent (18 pages)

- [x] **login.html** — real form, 2FA, lockout, forgot-password → pwreset (B6) *(S6; Identity staff
      sign-in with lockout, invalid-credential/locked-out states via `.rd-form-error` + shared `auth.*`
      keys; mockup's `<a>` sign-in → real `<button type=submit>` (+`width:100%`); mockup's in-card 2FA
      "önizleme" box renders as the separate `/agent/login/2fa` step (portal-pwreset multi-step
      precedent) reusing the box's exact DOM — mockup needs an S0-style pass to gain a real 2FA step
      card, drop "(önizleme)" from `lg.twofaCaption` (product copy strips it) and define the added
      `lg.backLogin` foot key; no remember-me — faithful, the agent mockup has none; E2E ids
      `#lg-user`/`#lg-pass` kept)*
- [x] **pwreset.html** — staff reset-link flow (request → mail → new password) (B6) *(S6; request card
      = mockup DOM, enumeration-safe, dev-mode reset link via TempData; sent state = `rc-notice` on the
      same page with added `pw.sentNotice` copy (invented, modeled on portal `pw.sub2`) — the mockup
      has no sent card, S0 to add or bless the notice; new-password step is an agent-conventions card
      (`rd-field`/`rd-control`, no portal "Adım 3 / 3" caption) with portal-copied `pw.*` strings —
      mockup lacks this card entirely, S0 to add; canon conflict: agent `pw.help` promises 30-min link
      validity vs portal's 1 h vs Identity's 1-day default — TODO(S7) at `PwresetCore`)*
- [x] **dashboard.html** — live stat tiles, tables from data with row deep-links, topbar search (B9) *(S6;
      tiles + tables query the signed-in staff's scope (assigned-to-me; solved/closed rows stay in the
      Taleplerim table per the mockup's solved row): my-open incl. updated-today note, due-today over
      DueDate??EstimatedDueDate, effort tile = my open tickets whose ACTIVE proposal is pending, tasks tile
      with overdue note (overdue derived IsOverdue || DueDate<now — seed sets no task flags). Hero R716555
      renders `effortWait` on the wait pill (§2 canon; portal Tickets derivation, overdue flag next in
      precedence, then status key). Tile notes with embedded counts are parameterized — TR copy deviates from
      mockup for suffix safety ("{0} tanesi bugün güncellendi" vs "2'si") — needs canon sign-off; due cells
      use invented db.dueToday/dueTomorrow/dueYesterday/db.dueDateFmt keys (mockup db.due*/task* sample-data
      keys dropped from resx, data comes from DB); table rows are the live top-5/top-3 by recency, not the
      mockup's exact sample rows. Deep links `/agent/ticket-view?id=`, `/agent/user-view?id=`,
      `/agent/task-view?id=`, `/agent/ticket-open` dead until those pages port (S5 precedent); "Tümünü gör" →
      /agent/tickets & /agent/tasks. B9 DONE here: (a) sidebar counts real — SidebarBadgeService replaces the
      stub ("tickets"=my open tickets, "tasks"=my open tasks, per-staff); (b) topbar search live on the agent
      panel — GET /agent/search (number-prefix ILIKE + tsvector via IQueueEngine, dept visibility applies;
      users name/email; orgs name; 5/group) filled into a debounce+fetch dropdown (rd.js) — chosen over a
      results page as the lighter invention since the mockups define NO search-results UI: dropdown design
      (app.css .bo-search-results) needs canon sign-off; admin topbar search + settings scope TODO(S7).)*
- [x] **live.html** — SignalR board: real column membership, drag between columns, Üstlen/claim, ticker from
      domain events, recoverable SLA countdown, pause (B7) *(S6; /agent/live + LiveBoardHub at
      /agent/live/hub (Staff policy; mounted under /agent so the Contextual cookie scheme + maintenance
      rewrite behave), signalr.min.js 10.0.11 vendored under wwwroot/lib. Membership real via
      LiveBoardEngine over QueueEngine visibility: Yeni=status "new"; Atanmamış=open-state unassigned
      minus "new"; Yanıt Bekleyen=status "wait"; SLA Riskli=IsOverdue flag OR due (DueDate??
      EstimatedDueDate) inside an INVENTED 10-min risk window (mockup samples imply <8 min, no canon —
      needs sign-off); Efor Onayında=active pending proposal — columns are independent queries (the
      mockup itself shows R716555 in two). Fan-out via per-department hub groups mirroring
      VisibleTicketsAsync (edge flagged: assigned-to-me-outside-my-depts and AssignedOnly agents can
      receive a ticker line for a dept ticket they can't open; their board refetch stays correctly
      filtered). LiveBoardHandler (Web/Services, EffortEmailHandler precedent) maps TicketCreated/
      Assigned/StatusChanged/Transferred/ThreadEntryAdded/Effort*/TicketOverdue → "BoardChanged"
      (clients refetch /agent/live/state, 200 ms debounce outliving the effort events' in-transaction
      dispatch) + "Ticker" items localized client-side (invented lv.evt*/lv.ago*/lv.err* TR-EN twins;
      TR assigned copy is suffix-safe "şu temsilciye atandı: X" vs mockup "Deniz Kaya'ya atandı" —
      needs canon sign-off; effort-reject line + ✗ emoji invented as the ✓ twin). Üstlen = ClaimAsync
      (vacation guard applies) + best-effort new→open transition so a claimed Yeni card moves; the
      mockup shows Üstlen on already-assigned cards too — kept for DOM parity, server refuses with the
      lv.errTaken toast. Drag = POST /agent/live/move: Yeni/Yanıt Bekleyen are status transitions,
      Atanmamış is a release (ticket.assign check); SLA Riskli/Efor Onayında are derived-membership →
      refused, card snaps back + toast; drag/drop styles added in app.css (mockup ships none). SLA
      chips tick every 6 s (lv.autoNote cadence) with warn<5 min/danger<1 min from the mockup JS (the
      static markup contradicts it — 12:37 rendered warn; the JS thresholds win) and RECOVER both on
      tick and on event re-render; chips render only where a due instant or overdue flag exists (most
      seeds have neither — EstimatedDueDate stays unset until the S8 SLA sweep, which also owns
      TicketOverdue: subscribed, never raised yet), remaining >1 h shows as total-minutes mm:ss. Pause
      buffers ticker items (cap 8) and collapses membership refetches into one on resume — flagged
      choice, the mockup simply drops its fake tick. Canlı Akış backfill reconstructs the last 6 items
      from thread_event rows + customer messages (domain events aren't persisted); Ekip Durumu = the
      active+visible roster with live open counts, away = Tatil Modu (canon defines no presence
      source — needs sign-off). E2E `LiveBoardTests` needs a freshly seeded DB (the single Destek
      claimable card is consumed per run — suite precedent, GoldenPath's hero step is equally
      one-shot). Differ 2026-08-18 PASS; canon question: mockup shows an SLA countdown chip on EVERY card, app only where a due instant exists (seed sparse until S8 EstimatedDueDate sweep).)*
- [x] **tickets.html** — queue tree filters for real (counts live, active state moves); B1 full list engine;
      advanced-search dialog: rule rows add/remove, column picker, sort, **save as queue**; export CSV; bulk bar
      *(S6; queue tree renders the seeded SavedQueue tree (+ the staff's personal queues under "Kayıtlı
      Aramalarım"), live counts via one QueueEngine COUNT per open-state child queue, memory-cached 30s per
      staff — closed windows/saved searches render without a badge, matching the mockup; ?queue= moves the
      active node, default = the assignee:me child ("Bana Atanan" per mockup). B1: header sort
      (updated/subject/priority/effort — effort via active-proposal-hours subquery) + toolbar sort select,
      status/priority multi-filters as autosubmitting rd-filter chips (effortWait/overdue filter on their
      derived flags), pagination 8/page, quick search composes with the queue via QueueCriteria.Search
      (tsvector ∩ queue criteria; number PREFIX search stays topbar-only — whole-token numbers match).
      Queue column configs honored (SLA VIP renders its 6 configured columns; "SLA Kalan" cell shows the
      due date, countdown decorator TODO(S7) with the queue builder). dlg-advsearch: rule add/remove
      (rd.js data-rule-add/.remove, B4 contract), 6 fields × 4 ops as free-text rules applied server-side,
      column picker (status has no checkbox → always on, as mocked), sort tab; apply = plain GET; save
      as queue creates a personal SavedQueue (flat-criteria keys where representable — status/dept/
      assignee/effort-pending resolved to keys/ids; org/date/not/contains stored as descriptive extra keys
      the engine logs-and-ignores by design, same as seeded SLA VIP). CSV export streams the current
      filter + visible columns (UTF-8 BOM). Bulk bar: Ata = assign-to-me and Durum Değiştir (via an
      INVENTED minimal status dialog dlg-bulk-status — mockup button had no dialog; needs canon sign-off)
      run per-row through TicketService with skipped-count toasts; Birleştir/Aktar/Sil disabled with
      explanatory titles — TODO(S6): merge service + transfer picker arrive with ticket-view, delete needs
      the deleted-state flow. Flagged deviations: queue titles are DB data — seeded canonical titles map
      onto the tq.q* keys for EN, custom queues render raw titles (needs canon sign-off); tq.pageOf
      parameterized "/ {0} talep"; invented keys tq.hoursShort/updToday/updYesterday/updDateFmt/colSla/
      bulk*/save*/notYet. Row/new-ticket deep links dead until ticket-view/user-view/org-view/ticket-open
      port (S5 precedent).)*
- [x] **ticket-view.html** — all header actions incl. Yazdır (print CSS) and Düzenle; effort propose/revise/
      withdraw + 3 banner states + thread events (B8); reply/note composers append; canned insert; signature
      preview; after-reply status; attach list; Diğer menu actions (merge/link/release/ban/delete…) implemented;
      thread ⋯ menus; related-tickets link dialog (B2/B5)
      *(S6 partial; /agent/ticket-view?id= with QueueEngine department visibility (list-page scope), NavKey
      tickets. DONE — thread: ALL entry types incl. internal Notes with the mockup's is-note styling, timeline
      events interleaved from thread_event rows (all seeded kinds, invented tv.evt* labels; assigned-with-nulls
      renders as released), per-entry attachment chips download through IFileStore staff-gated; hero R716555
      renders its pending 6h card. DONE — B8 flagship: dlg-effort proposes, or REVISES while a proposal is
      pending (header button, banner "Revize Et" and rejected-banner "Yeni Öneri Gönder" all reuse it, mockup
      wiring); "Geri Çek" withdraws; all three banner states live (approved/rejected variants taken from the
      mockup's HTML comments); every transition writes its thread event + AuditEvent via EffortProposalService;
      guard violations (state machine, revision limit, effort.propose permission) surface as error toasts;
      tv.effPendingTitle/tv.effApprovedMsg + the three tab labels are parameterized "{0}" versions of the
      mockup's sample-data copy — needs canon sign-off. DONE — B5 composers: reply posts a staff Response via
      IThreadService ("text" format; B8 work gate → tv.errWorkBlocked error toast), From select = EmailAccounts
      with the dept account preselected and a recipients/from JSON snapshot on the entry, attachments via
      IFileStore with an added .rc-file-name span (mockup lacks one — B5 requires the list); note composer
      appends Note with optional title; after-reply/note status select (open/wait/solved rows per mockup)
      applies via TransitionStatusAsync — refusals (B8 close gate/permission) keep the entry and toast
      tv.errStatus. Canned select = dept+global CannedResponses from DB plus the mockup's Orijinal/Son Mesaj
      quote options; expansion server-side (CannedResponseService variables) → rd.js data-canned-insert inserts
      at the caret. Signature radios preview the staff/department signature into an added rd-help block —
      NOTE(S8): applying the chosen signature happens at outbound-mail render, nothing to persist yet.
      DONE — header: Aktar/Ata dialogs drive TransferAsync/AssignAsync (staff "s:{id}"/team "t:{id}" options;
      the optional comment posts as an internal note, osTicket parity); Yazdır = rd.js [data-print] +
      product-only @media print block in app.css (mockups define NO print CSS — design needs canon sign-off).
      DONE — Diğer: Serbest Bırak (AssignAsync null/null), Gecikmiş İşaretle (flag + "overdue" event under the
      actor's audit scope — no dedicated S4 service exists for the flag), Yanıtlandı İşaretle (flag,
      ticket.markanswered check). TODO(S6): Düzenle disabled — needs the built-in ticket edit form (S7 form
      designer feeds it; mockup defines no edit dialog); Diğer items Sahibi Değiştir/Birleştir/Bağlantıla/
      Yönlendirmeler/Katılımcılar/E-posta Engelle/Talebi Sil disabled with explanatory titles (no merge/link/
      ban/delete services yet; Formları Yönet is TODO(S7)); related-tickets tab renders the ParentId family
      (seed links none → mockup empty state) but the LINK DIALOG is TODO(S6) with the merge/link service —
      empty-state button disabled; thread ⋯ entry menus TODO(S6) (needs a thread-edit service; the mockup
      defines no menu DOM) — buttons disabled, never dead-looking. Invented keys: meta.today/yesterday,
      tv.dateLong/dateShort/slaFmt/hoursFmt/source*/evt*/toast*/err*/notYet (TR+EN twins). Tests:
      AgentTicketViewTests — effort propose→revise→withdraw through the controller path incl. thread events,
      composer Response/Note append + after-reply status.)*
- [x] **ticket-open.html** — user autocomplete + inline "Yeni Kullanıcı"; topic→dept/SLA/form cascading;
      attach list; creates ticket on behalf of user (B3/B5) *(S6; /agent/ticket-open. DONE — user block: search
      input debounce+fetches /agent/ticket-open/users (SearchController ILike patterns + phone per the mockup
      placeholder) into a .bo-search-results dropdown of buttons (mockup defines NO dropdown DOM — reused the
      topbar-search panel, needs canon sign-off); picking fills "Name — email" + hidden UserId. "Yeni Kullanıcı"
      toggles an ADDED inline name/email/phone block (mockup has only the button — DOM flagged for canon
      sign-off); submit creates a guest User+UserEmail, org auto-linked by email domain (AccountController.
      LinkDomainUserAsync pattern), IdentityUserId null; duplicate email → errEmailInUse, blocked user →
      errBlocked. DONE — cascade: topic options carry data-dept/priority/sla and drive the dept/SLA/priority
      selects client-side + swap the topic's HelpTopicForm fields (portal /open hidden+disabled precedent,
      agent field scope VisibleToAgents/RequiredForAgents); server cascade in TicketService.CreateAsync stays
      authoritative, so a hand-picked dept/SLA only applies when the topic has no override (osTicket parity —
      flagged). SLA options = active plans as "Name (N saat)" (to.slaFmt); dept/SLA preselect = core.default_*
      settings. DONE — create: ITicketService.CreateAsync with ActorContext.ForStaff (audit + events + permission
      pre-flight on the effective dept BEFORE the guest user is created), source select phone/email/web/other →
      TicketSource, due date, attachments via IFileStore onto the initial Message (+added .rc-file-name span,
      B5), FormEntry/Values persisted, CC field → find-or-create guest users + ThreadCollaborator (Cc role) via
      IThreadService.AddCollaboratorAsync; Ata select ("s:/t:" options) → AssignAsync post-create (refusal does
      not fail creation). Advanced: first response posts a staff Response ("text" + recipients snapshot), iç not
      posts a Note; canned select inserts via /agent/ticket-open/canned — body returned UNEXPANDED (%{vars}
      resolve only against an existing ticket; page-scoped handler because rd.js data-canned-insert targets the
      form's first textarea = details). Redirects /agent/ticket-view?id=. NOTE(S8): Bildirim radios + signature
      choice accepted but only consumed when outbound mail lands (sig radios preview via data-sig-text, dept sig
      follows the dept select). Layout gained an optional ViewData["MainStyle"] so <main> carries the mockup's
      max-width:880px. Invented keys (TR/EN twins): to.err*/newUser*/noUserResults/slaFmt/grpAgents/grpTeams —
      grp* localize the mockup's hardcoded optgroup labels "Temsilciler"/"Takımlar" (mockup bug: not data-i18n).)*
- [x] **tasks.html** — tab bar (fixed in S0) filters from data; B1 engine; new-task dialog creates *(S6; new
      TaskService (Infrastructure) carries create/close/assign/transfer/edit/delete with the task.* permission
      keys, task-sequence numbering (settings tasks.*), thread events + AuditEvent via interceptor, and a
      VisibleAsync scope mirroring the QueueEngine ticket rule (own depts ∪ assigned-to-me; AssignedOnly
      honored). Tabs (Açık/Görevlerim/Gecikmiş/Tamamlanan) filter from data with live counts; the mockup's
      client data-tabs become server round-trip links (only the active panel renders — agent-login <a>→<button>
      deviation precedent); "mine" stays the default active tab per the mockup. B1: header sort on
      no/date/title/dept/assignee (date desc default), quick search (number/title ILIKE), pagination 8/page
      (tickets parity; the mockup pagination is sample data). Toolbar Ata = bulk assign-to-me (tickets-toolbar
      precedent), Aktar/Sil = invented bulk dialogs (dlg-bulk-status precedent; Sil hard-deletes via service,
      skipped rows reported) — need canon sign-off. dlg-newtask creates a real TaskItem (title/dept/assignee/
      due/description; description → first thread entry per ta.fDescHelp) and redirects to its task-view; the
      mockup's hardcoded due value 2026-08-18 is treated as sample data (input renders empty). Invented
      (TR/EN twins): ta.dateFmt/emptyTitle/emptyText/bulk*/deleteConfirm; rd-empty state invented per B1 (the
      mockup defines none). Overdue = IsOverdue || DueDate&lt;now (dashboard derivation).)*
- [x] **task-view.html** — header actions (Kapat/Ata/Aktar/Düzenle/Sil) via dialogs; note composer appends (B2/B5)
      *(S6; header/meta/thread from data; status pill derived open/overdue/closed. The five header buttons open
      invented B2 dialogs (the mockup's buttons are dead — ticket-view dialog DOM reused; need canon sign-off):
      Kapat = close + optional internal note; Ata reuses the s:/t: assignee encoding (staff + teams); Aktar =
      dept select; Düzenle = plain fields only (title + due; dept/assignee live in their own dialogs — no
      form-designer scope needed); Sil = confirm → HARD delete of task + thread (osTicket parity; the entity
      has no soft-delete flag) → back to /agent/tasks. All actions post through TaskService with
      ActorContext.ForStaff; permission failures toast tav.errDenied (buttons stay enabled per mockup, service
      enforces task.* keys). Closed tasks disable Kapat (tav.closedInfo title) — the mockup defines no reopen
      control, TODO(S7) if canon wants one. Note composer appends via IThreadService (Note, "text"); no attach
      control in the mockup → none rendered. Thread interleaves timeline events (ticket-view is-event row —
      the task mockup shows entries only, seed adds no events; service actions add them). Ticket link renders
      both ways (İlgili Talep here ↔ ticket-view Görevler tab). Creator line: the entity has no creator field —
      tav.createdLine renders created date · assignee (mockup sample shows the assignee name); needs canon
      sign-off. Invented (TR/EN twins): tav.pageTitle/createdLine/date*/timeFmt/meta*/editTitle/deleteConfirm/
      closedInfo/evt*/toast*/err*. Tests: TaskServiceTests (7) cover create/close/assign/transfer/edit/delete
      incl. permission denials.)*
- [x] **users.html** — B1 engine; add-user dialog creates; "Diğer" bulk menu actions real; CSV import *(S6; new
      UserService (Infrastructure) carries create/update/override/block/org/delete/note/CSV-import with the
      user.edit / user.manage keys checked "anywhere" (users are non-departmental; Temsilci holds neither) and
      AuditEvent via the interceptor. B1: header sort name/email/org/reg/updated (updated desc default), quick
      search (name/email ILIKE), pagination 8/page, row checkboxes + select-all feeding the us-bulk carrier.
      Tabs are server round-trip links with live counts (tasks precedent); the Arşiv tab honestly counts 0 and
      renders the mockup's rd-empty — the entity has NO archive flag (hard deletes only, osTicket parity), so
      an archive mechanism needs a canon decision if the tab should ever fill. dlg-adduser creates a real
      User+UserEmail via UserService (explicit org select wins, else Organization.Domain auto-link per
      us.fOrgHelp; duplicate email → us.errEmailInUse error toast) and redirects to the new user-view.
      "Diğer" = the mockup's bulk dialog: Kilitle/Kilidi Aç drive IsBlocked; Şirkete Ekle chains into an
      INVENTED org-select dialog dlg-addorg (B2, tasks dlg-bulk-transfer precedent — needs canon sign-off);
      Sil chains into an INVENTED confirm dialog, hard delete with a service guard — users with tickets or
      collaborator rows are skipped and reported (tickets.user_id FK is Restrict); submit buttons drop the
      mockup's data-dialog-close (the audited B2 close-swallows-save bug). Parola Sıfırlama Gönder + Kaydet
      disabled with us.notYet titles — both need outbound mail / an invite service (TODO S8). CSV import: the
      dead İçe Aktar button gains data-dialog-open onto an INVENTED dlg-import (admin/list-edit dlg-import
      DOM with a file input, B2/B5 — needs canon sign-off); strict name,email[,org] parser (optional header,
      quoted fields) creates users with org-by-name or domain auto-link and toasts created/skipped counts.
      Invented keys (TR/EN twins): us.dateFmt/updToday/updYesterday/emptyAll*/bulk*/deleteConfirm/import*/
      err*/notYet/toastDeleted; rd-empty for the all-tab search miss invented per B1 (mockup only defines the
      archive variant). Org cells deep-link /agent/org-view?id= — live since the org pages ported (same S6).
      Tests: UserServiceTests (9).)*
- [x] **user-view.html** — header actions; "Geçersiz kıl" inline override of inherited fields; tabs from data;
      note composer (B3/B5) *(S6; /agent/user-view?id=. Profile card from data; osTicket user/organization
      field inheritance implemented for the two fields the mockup badges: User.Phone + new User.Address
      (S6_Users migration) are per-user overrides; null inherits Organization.Phone/Address with the
      "Şirketten" badge, and "Geçersiz kıl" opens an INVENTED inline value+save form (B3; app.css
      .uv-override-form — needs canon sign-off). Blank saves revert to inherited; dlg-edit's phone/address
      fields (blank = inherit, uv.fInheritHelp) are the revert affordance. CANON CONFLICT flagged: hero
      Bourla renders his §2-canon domain Phone +90 212 555 0180 as a per-user value WITHOUT the badge — the
      mockup badges the phone as inherited, but Ulaşım's seeded/org-view phone is +90 212 555 0142, so both
      pages cannot be right at once; needs a §2 decision (align the org phone or drop the badge). Account
      card honest: login method = uv.loginEmail vs invented uv.loginGuest; 2FA pill from
      CustomerUser.TwoFactorEnabled (Etkin/Kapalı); Son giriş renders "—" — customer sessions are not
      tracked yet (TODO with B6 session work; mockup's IP line has no data source). Tabs keep the mockup's
      client data-tabs with live counts: tickets tab = this user's tickets from data (overdue-wins derived
      status, ticket-view deep links), notes tab = NEW UserNote rows — the mockup's note cards carry
      author+badge+time, which the legacy User.Notes text blob cannot, so UserNote (user_notes table) is the
      chosen smallest faithful mechanism, seeded with the canon Merve Çetin 4 Ağu 2026 note; org-view's
      composer should reuse it when that page ports. Note composer appends via UserService.AddNoteAsync (B5)
      and reopens the Notlar tab. Header: Düzenle = INVENTED dlg-edit (name/phone/address/org), Hesabı
      Yönet = INVENTED dlg-manage (Kilitle/Kilidi Aç per current state; broader account management lands
      with S8 invites), Sil = confirm dialog → hard delete incl. the portal Identity account, refused with
      uv.errHasTickets while tickets/collaborations exist; Parola Sıfırlama Gönder disabled (uv.notYet,
      TODO S8). All mutations run through UserService with ActorContext.ForStaff (+ audit scope). Invented
      keys (TR/EN twins): uv.pageTitle/stGuest/stLocked/loginGuest/twofaOff/dateLong/noteTimeFmt/upd*/
      noTickets/fName/fInheritHelp/fOrgNone/manageHelp/mLock/mUnlock/deleteConfirm/err*/notYet/toast*.)*
- [x] **orgs.html** — B1 engine; add-org dialog creates; export *(S6; new OrgService (Infrastructure,
      UserService shape) carries create/update/sync-flags/note/delete behind org.edit checked "anywhere"
      (orgs are non-departmental; no finer org.manage/org.delete key exists in the seeded role matrix —
      needs a canon/roles decision if delete should be scoped tighter). B1: header sort
      name/sector/users/open/manager/updated (name asc default; users/open/updated default desc), quick
      search (name/sector/domain ILIKE), pagination 8/page; row checkboxes + select-all render per mockup
      but drive nothing — the mockup defines no bulk bar. Kullanıcı Sayısı / Açık Talep are live counts
      (members; member tickets in an Open-state status). NEW Organization.Sector column (S6_Orgs migration)
      backs the Sektör column/dialog field — the seeder's "Sektör:" Notes prefix moved there; org seed rows
      also gained canon CreatedAt values so the Updated column reads like the mockup. dlg-addorg creates a
      real Organization via OrgService (duplicate name → og.errNameInUse; domain input normalized "@"-less
      comma-list so UserService auto-link matches; invented "—" manager option, us.fOrgNone precedent) and
      redirects to the new org-view. Dışa Aktar = CSV of the current search+sort, all pages (tickets export
      precedent; sirketler.csv). Invented keys (TR/EN twins): og.fManagerNone/dateFmt/updToday/updYesterday/
      emptyTitle/emptyText/err*/toastDeleted; rd-empty for the search miss invented per B1 (mockup defines
      none). Tests: OrgServiceTests (7).)*
- [x] **org-view.html** — header actions; sync switches persist + banner reflects actual sync; tabs from data;
      note composer *(S6; /agent/org-view?id=. Profile card from data (domain pills, phone/address, manager);
      SLA Planı renders "—" — the entity has no per-org SLA (osTicket parity: SLA lives on topic/dept/ticket)
      and the mockup's "Kurumsal (8 saat)" also conflicts with the §2 SLA canon — needs a canon decision;
      Zaman dilimi renders the single-tenant company zone (Europe/Istanbul, user-view fallback convention).
      SYNC CARD DEVIATION (needs canon sign-off): the mockup's two switches (domain auto-link, field push)
      map to no entity flag — the card instead persists the three real osTicket flags
      ShareTicketsWithMembers / CcPrimaryContacts / AssignToManager as auto-submitting switches (B3,
      invented ov.swShare/swCc/swAssign keys; ov.sw1/sw2 remain in the resx unused), and the success banner
      + "Son senkronizasyon" line render from the actual saved state (PRG flag + UpdatedAt + live member
      count) instead of the mockup's static show-on-any-click "14". Tabs from data with live counts: members
      5/page (mockup pagination), user-view deep links, phone inheritance badge (null Phone → org phone +
      Şirketten); tickets = org-wide via members (overdue-wins derived status, ticket-view links); notes =
      NEW OrgNote rows — deliberately a parallel twin of UserNote (same columns, S6_Orgs migration) rather
      than generalizing to a polymorphic note table (smaller change, no user_notes churn), seeded with the
      canon Ümit Yaşar Akın 1 Ağu 2026 note (moved out of the Ulaşım Notes blob). Composer appends via
      OrgService.AddNoteAsync (B5) and reopens Notlar. Header: Düzenle = INVENTED dlg-edit
      (name/domains/sector/manager/phone/address; user-view dialog DOM); Formları Yönet disabled (ov.notYet,
      needs the S7 form designer); Sil = confirm dialog → hard delete (notes cascade), refused with
      ov.errHasMembers while members or member tickets exist. All mutations run through OrgService with
      ActorContext.ForStaff (+ audit scope). Invented keys (TR/EN twins): ov.pageTitle/stLocked/dateFmt/
      updToday/updYesterday/syncToday/syncYesterday/noteTimeFmt/sw*/fName/fDomainHelp/fSector/fManagerNone/
      noUsers/noTickets/notYet/deleteConfirm/err*/toast*; ov.meta/syncBanner/lastSync resx values carry
      {0}-style holes for the live numbers.)*
- [x] **kb.html** — category tabs (fixed in S0) filter; search; manage-categories dialog CRUD *(S6;
      /agent/kb. New KbService (Infrastructure, faq.manage checked "anywhere" — seeded on Yönetici +
      Kıdemli Temsilci; AuditEvents via actor scope). Tabs = server round-trip links (tasks precedent),
      per-category counts live; search = the portal KB ILike over question/keywords/answer, composing
      with the active tab; agent scope lists drafts too (Dahili pill = IsPublished false). rd-empty on
      search miss invented per B1 (mockup defines none). Manage-categories dialog: add is real; the
      static name+count rows became inline rename forms with Kaydet + ✕ delete (INVENTED row UI —
      needs canon sign-off); delete refused with kb.errCatHasArticles while the category has articles
      (honest guard vs osTicket's cascade — flag for canon). The mockup's stray extra </div> after
      kb-panel-sistem is not reproduced (only the active panel renders). Portal-side notes carry over:
      category names are DB data (stay TR in EN), no per-article summaries. Invented keys (TR/EN twins):
      kb.catNameLabel/emptyTitle/emptyText/toastCat*/toastArticleDeleted/errCat*/errInvalid/errDenied.)*
- [x] **kb-faq.html** — saves; attachments upload with file list (input added in S0); preview; delete (B2/B5)
      *(S6; /agent/kb-faq (new) + ?id= (edit). Saves create/update real FaqArticles via KbService; the
      kf-listing radios persist as IsPublished + new FaqArticle.IsFeatured (osTicket ispublished=2
      "Featured", implies published; S6_KbFeatured migration — no canon article seeds featured: the hero's
      radio is Herkese Açık and no mockup lists one); Yardım Konuları multi-select persists FaqArticleTopic
      rows ("Parent / Child" labels, ticket-open precedent); Notlar persists FaqArticle.Notes (hero note
      seeded from this mockup's textarea). Answer stays stored HTML under a plain textarea: tag-less input
      is paragraph-wrapped on save, HTML passes through sanitized — the hero article therefore shows its
      seeded HTML source when edited (mockup shows prose; rich editor TODO(S7)). Attachments (B5): S0 file
      input uploads on save (added .rc-file-name span, ticket-view precedent), list + staff download, per-file
      ✕ delete via external form (INVENTED — mockup chip is static) — all through IFileStore; the mockup's
      sample chip "yol-ucreti-ornek-hesap.xlsx" conflicts with portal kb-article's two canon files (portal
      canon kept, needs §2 decision). Önizle = client-filled preview dialog of the current editor content
      (INVENTED — mockup button dead; needs canon sign-off); Sil = B2 confirm dialog → article + attachments
      deleted, lands on /agent/kb; disabled on a new article. Header meta composed from data — the static
      kf.meta sample string stays unused in the resx. Invented keys (TR/EN twins): kf.newTitle/metaNew/
      metaUpdated/dateFmt/deleteConfirm/toast*/errInvalid/errDenied.)*
- [x] **canned.html** — B1 engine; per-row edit dialog prefilled (B2); create/disable/delete
      *(S6; new /agent/canned (CannedController) over the existing CannedResponseService — CRUD added there
      (create/update/enable-disable/hard delete), gated by canned.manage checked anywhere (KbService
      faq.manage precedent; Temsilci lacks it, Kıdemli+ hold it); expansion/composer-insert untouched. B1:
      header sort title/dept/updated (updated desc default per the mockup), search ILike over title+body,
      pagination (PageSize 8, orgs precedent); rd-empty on search miss invented per B1 (mockup defines none).
      B2: the mockup's ONE dlg-canned shared by the new-button and all 5 rows split into a create dialog
      (mockup DOM verbatim) + per-row edit dialogs prefilled server-side — edit title cn.dlgEditTitle and an
      Etkin rd-check (INVENTED, needs canon sign-off: without it disabled rows could never be re-enabled;
      the mockup only offers bulk Devre Dışı Bırak). Body: plain textarea over stored HTML, kb-faq answer
      precedent (tag-less input paragraph-wrapped, HTML sanitized; %{variables} survive; raw HTML shows when
      editing seeded rows — rich editor TODO(S7)). Toolbar bulk buttons act on the checkbox selection via
      the hidden cn-bulk form (users precedent): Devre Dışı Bırak posts directly, Sil = INVENTED confirm
      dialog (tasks/users precedent) → HARD delete (tasks Sil precedent — nothing references a canned
      response after insert; flagged for canon). Disable/enable verified to gate the ticket-view/ticket-open
      composer selects (ListForAsync + the ticket-open query both filter IsEnabled — no change needed).
      Dept cell renders cn.fDeptAll for department-less rows (none seeded; canon seeds 7, all dept-scoped).
      Duplicate-title guard title-in-use (org name-in-use precedent). Tool fix ridealong: i18n-convert's
      lang-section regex stopped at the first '}' — inside %{ticket.number} of cn.fBodyHelp — silently
      dropping the key; closing brace now anchored to its own line (matters for S7 template pages).
      Invented keys (TR/EN twins): cn.dlgEditTitle/fEnabled/fEnabledHelp/deleteConfirm/emptyTitle/emptyText/
      dateFmt/updToday/updYesterday/toastCreated/toastSaved/errTitleInUse/errInvalid/errDenied/bulkNone/
      bulkDone/bulkPartial. Differ 2026-08-18: page 500ed in all states — cn.fBodyHelp resx value carried unescaped %{var} braces and IHtmlLocalizer string.Formats every value → FormatException mid-response; braces now escaped {{ }} and check-i18n.mjs gained an unescaped-brace guard; dialogs re-diffed after the fix.)*
- [x] **directory.html** — search + department filter work; mailto/tel links *(S6; presence from real sessions still TODO — current pills are a stub: LastLoginAt/OnVacation-derived; B1 header sort TODO)*
- [x] **profile.html** — saves; 2FA setup ("Yapılandır"); vacation switch has effect (assignment guard);
      signature editor; language select switches culture *(S6; /agent/profile (new ProfileController,
      Staff policy, own row only — no staff-id parameter). Saves: contact/auth/prefs/signature persist on
      Staff (S6_StaffPrefs migration: TwoFactorMethod, PasswordChangedAt, PageSize, AutoRefreshMinutes,
      DefaultQueue, ThreadOrderNewestFirst, Use24HourTime) with Identity email + FullName-claim sync;
      page-size/refresh/queue/thread-order/time-format/DefaultSignatureType persist but their list-engine/
      rendering consumption is TODO(S7) with the settings round-trip work. Language: portal precedent —
      data-lang-switch submits the form, culture cookie server-side; staff login now also applies the
      persisted language. Password dialog = portal B3 (client match+strength, server current-password
      check; PasswordChangedAt feeds pf.passHelp's new {0} hole — seeded 12 Haz 2026 for uakin only,
      help line hidden while null). 2FA: pf-2fa select is real — Yapılandır opens dlg-2fa (INVENTED DOM,
      needs canon sign-off: manual key + otpauth link + code confirm per the admin 2fa-setup precedent;
      no QR image, client-side QR is S7 if canon wants one); "E-posta kodu" is a working method (login
      sends the code via the Email token provider; Login2fa help swaps to invented lg.twofaHelpEmail on
      both staff login cards); selecting the app method without a confirmed enrollment is refused
      (pf.err2faNeedsSetup), and admins cannot disable 2FA (AdminOnly mfa policy — pf.err2faAdminRequired,
      flag for canon). Vacation switch persists OnVacation and gets its guard: StaffAvailability throws
      staff-on-vacation in Ticket/Task Assign/Claim/Create (bulk paths report it as skipped), topic
      auto-assign drops the staff pin but still routes, and the four assignee option lists (ticket-view,
      task-view, tasks, ticket-open) exclude vacationing agents while keeping a current assignee visible —
      consistent with dlg-assign's canon roster (no Selin Aydın). Existing assignments stay put:
      pf.vacationHelp's "mevcut talepleriniz kuyruğa alınır" promise is NOT implemented — flag for canon.
      Signature textarea persists Staff.Signature, which the ticket-view/ticket-open composers already
      consume via data-sig-*. Seed: uakin gains Mobile, the profile signature text,
      DefaultSignatureType=Mine and AutoRefresh=1 per this mockup (TwoFactorMethod=App only when the dev
      AdminTotpKey enabled TOTP). Invented keys (TR/EN twins): pf.dateFmt/dlg2fa/dlg2faKey/dlg2faHelp/
      dlg2faLink/dlg2faCode/toastSaved/toastPass/toast2faOn/err2faCode/err2faNeedsSetup/
      err2faAdminRequired/errCurrentWrong/errPasswordWeak/errPasswordMismatch/errInvalid/errEmailInUse
      + lg.twofaHelpEmail. +9 tests (AgentProfileTests, incl. a full-pipeline render smoke) — suite 144. Differ 2026-08-18 PASS; canon note: seeded username uakin vs mockup umit.akin (login name choice — needs canon call).)*

### 6.3 Admin (42 pages)

Shared: every list page gets B1 (sort/search/pagination/selection/bulk/empty state), every dialog B2
(parameterized + correct submit), every settings page B3 (gating) + B9 scroll-spy, builders B4.

- [x] **login.html** — real form, mandatory 2FA, forgot-password (B6) *(S7; verify-and-complete over the
      S2/S6 StaffAccountControllerBase: admin Identity sign-in (auth.notAdmin role gate, `.rd-form-error`
      states) was already live — parity fixes: `required` on both inputs, 2FA card margin-top/maxlength 6,
      foot link was lg.backAgent copy pointing at /admin/login → now invented lg.backLogin "← Girişe dön"
      (agent Login2fa precedent). Mandatory 2FA is real end-to-end: password-only success of a no-2FA
      admin lands on /admin/2fa-setup (INVENTED page, S2 — mockup has no enrollment card; now shows the
      otpauth link via new shared twofa.link, profile dlg-2fa precedent), the staff cookie's access-denied
      handler reroutes every /admin/* attempt back to setup until a TOTP code confirms (then sign-out →
      re-login with amr=mfa for AdminOnly); already-enrolled admins get redirected to the dashboard, and a
      2fa GET without a pending password step bounces to login. Mockup's in-card "(önizleme)" box stays
      the separate /admin/login/2fa step (S6 agent precedent); email-code method honored via
      Staff.TwoFactorMethod. +2 tests (AdminAuthTests).)*
- [x] **pwreset.html** — admin reset-link flow, admin-only lockout policy (B6) *(S7; request card = mockup
      DOM; sent state = `rc-notice` with invented pw.sentNotice (agent-precedent copy — mockup has no sent
      card, S0 to add or bless); flow is admin-gated: non-admin emails get the same enumeration-safe
      notice but no mail; new-password step = new admin PwresetNew card (agent-conventions twin — the
      mockup lacks this card entirely, S0 to add). Reset-validity canon: this mockup's pw.help + the
      settings-agents sa.resetWindow default agree on 30 min → staff reset links now really expire in
      30 min (StaffResetTokenProvider; config StaffAuth:ResetWindowMinutes; RESOLVED when settings-agents
      ported: agents/reset_window_minutes owns the value, config is the fallback only; portal's 1-hour
      pw.help promise still sits on Identity's 1-day default — open customer-side canon item). Lockout —
      RESOLVED toward the mockup when settings-agents ported: the interim INVENTED admin-only tightening
      (3 attempts, keys agents/admin_max_login_attempts + admin_lockout_minutes) is dropped; the policy is
      STAFF-WIDE per the mockup's fields — agents/max_login_attempts + lockout_minutes, defaults 5/30
      (sa.maxAttempts/sa.lockDuration selected states), applied at BOTH sign-ins (password or 2FA code);
      staff-wide duration moved 15 → 30 with it; shared auth.lockedOut copy de-hardcoded from
      "15 dakika" to duration-neutral. +5 tests (AdminAuthTests) — suite 167.)*
- [x] **dashboard.html** — date range re-renders data-driven charts with tooltips; export; 3 stats tables sortable (B10)
      *(S7; DashboardEngine (TicketListEngine-style split for testability): range resolution (start + 30d/
      quarter/year — "Bu çeyrek"/"Bu yıl" = CALENDAR period of the start date, chart capped at today —
      needs canon sign-off), daily/weekly/monthly buckets reproducing the mockup's Oca..Ağu year-to-date
      axis, NiceMax y-grid. Metric mapping: Açılan=CreatedAt, Çözülen/Kapatılan=ClosedAt, Atanan="assigned"
      thread events (sparse in seed — only the hero's), Geciken=IsOverdue on opened-in-range, Yeniden
      Açılan=ReopenedAt, Servis=avg(close−create), Yanıt=avg(first staff Response−create), SLA Uyumu=%
      closed on/before due; tile deltas vs equal-length previous period. Chart SVG generated with the
      mockup's exact structure from real aggregates; native <title> hover tooltips INVENTED (B10 asks for
      tooltips, mockup has none). 3 tables: 5 sortable columns = B1 server-sort links, tab preserved via
      ?tab=. Export = ONE CSV of all three tables w/ Grup column (UTF-8 BOM). Invented keys: db.mo9–12,
      pctFmt, statNoPrev, durHm/durM/hoursFmt, chartAria (mockup hardcodes a TR aria-label — mockup bug),
      colGroup; tile notes parameterized "geçen döneme göre {0}". Canon notes: agent table keeps the
      mockup's FULL staff names (vs §2 short-name cell convention); thread events don't snapshot dept/
      topic/assignee so groups use the ticket's CURRENT values (deviation from osTicket per-event
      snapshots); no empty-state variant (mockup defines none).)*
- [ ] **system-info.html** — real server/PHP→.NET runtime/db info; update check
- [ ] **system-logs.html** — filters + Apply work; purge deletes; row detail dialog (B1/B2)
- [ ] **audit-logs.html** — filters/export/pagination; rows drill down to the audited object (B10)
- [x] **settings-company.html** — logo file uploads with preview (B3); page selects live from SitePages
      *(S7; /admin/settings-company (SettingsCompanyController) over ISettingsService ns "company" —
      PRG toasts, B3 server validation writes NOTHING on failure (sc.errValues/errLogo/errLogoFile).
      LIVE: page selects list REAL SitePage rows per type (the S3 entity + S5 seed already carried the
      pages.html canon rows Hoş Geldiniz/Bakım Modu/Talep Alındı/KVKK — no new migration); inactive pages
      stay listed (the canon offline page is disabled — an active-only filter would empty its select);
      the mockup's second options ("Bordro dönemi duyurusu", "Hafta sonu mesajı", "SLA bilgilendirmeli")
      are INVENTED sample state absent from the pages.html canon table (flagged). Logo/backdrop uploads =
      B3 for real: default/custom radios gate the .rd-upload zones (NEW rd.js radio-group data-gates
      resync — checking the sibling radio now re-syncs), files stream into IFileStore (kb-faq precedent),
      preview = GET /admin/settings-company/logo?which=client|staff|backdrop serving the stored file back
      (no base64 anywhere; replaced files are deleted from store+db); PNG/SVG ≤ 2 MB enforced from the
      sc.upload canon text; custom mode without any file refuses (sc.errLogo). PERSISTED-ONLY (annotated
      at the VM): company name/website/phone/address TODO(S8 %{company.*} template variables; no portal
      footer exists to consume them), landing_page_id (portal home content block — pages port),
      offline_page_id (offline view serves the S5 static twin today), thanks_page_id (post-create
      confirmation; HelpTopic.SitePageId already overrides per topic), logo modes/file ids (portal
      header + backoffice topbar logo swap and the agent login backdrop are TODO — the preview endpoint
      is AdminOnly; a public branding route needs the maintenance-middleware pass-through list, flagged).
      Canon flags: no removal UI for an uploaded backdrop/logo (mockup defines none — a new upload
      replaces, default radio falls back for logos); sc.addressHelp's %{company.address} brace-escaped in
      resx (string.Format hazard). Invented keys (TR/EN twins): sc.toastSaved/errValues/errLogo/errLogoFile.
      Tests: SettingsSystemCompanyTests — SitePage options + save round-trip, logo upload→setting→preview
      byte round-trip + 2 MB refusal.)*
- [x] **settings-system.html** — language add/remove rows (B4); attachment storage settings consumed; maintenance mode drives portal/offline
      *(S7; /admin/settings-system (SettingsSystemController) over ISettingsService (ns "system"/"core"/
      "attachments"; NEW AttachmentSettings typed section) — PRG toasts, B3 validation writes NOTHING on
      failure (ss.errValues). LIVE (5): online switch reads/writes the SAME system/offline Setting the S5
      MaintenanceModeMiddleware serves portal/offline from (inverted; HTTP-POST flip test: portal serves
      the offline card, /admin keeps rendering, flip back reopens); helpdesk name = core/helpdesk_title
      NEWLY consumed by all three layouts' browser-tab <title> (TODO(S8): outgoing-mail sender name);
      default department = core/default_dept_id (TicketService.CreateAsync cascade, consumed since S4);
      primary language NEWLY consumed as the request-culture fallback after the cookie (Program.cs
      CustomRequestCultureProvider — explicit user choice still wins); attachments max_size_mb enforced at
      EVERY upload ingress (portal open + portal reply, agent reply, agent ticket-open, kb-faq save — the
      whole POST refuses with invented errAttachTooBig keys on each page's resx, nothing partial persists).
      B4 language rows: system/secondary_languages csv; row ✕ and "Dil ekle" are real server round-trips
      (POST /admin/settings-system/languages via the form= attribute — INVENTED mechanics, the mockup's
      buttons are dead; dlg-seq separate-POST precedent); the add select offers the catalog minus primary
      minus current rows (mockup statically lists de/fr/ar); de/fr/ar persist with TODO: i18n resources —
      only TR/EN resx exist, extra languages have no UI translations (canon flag); a primary sitting in the
      secondary list is dropped on save. PERSISTED-ONLY (each annotated at the VM build): helpdesk_url
      TODO(S8 email link base), force_https (env-gated UseHttpsRedirection today), collision_minutes
      (composer lock TTL — IThreadService lock API unwired), page_size (TicketListEngine.PageSize const),
      log_level + log_purge_months (system-logs port / S8 retention job), show_avatars (thread renderers),
      rich_text (composer toolbar gate), iframe/embed allowlists + acl_ips/acl_scope TODO(S9 hardening),
      locale/timezone/time_format_mode + 4 patterns (display-formatting helpers), default_schedule_id (SLA
      fallback calendar; unset renders the default SLA's schedule); attachments storage select persists but
      only the "fs" backend is registered — the mockup's selected "Veritabanı" is sample state CONFLICTING
      with the dropped file_chunk canon (StoredFile), honest default shows Disk (flagged); auth_required
      persists (every download endpoint already sits behind auth; the OFF state needs an anonymous KB route
      once a public KB exists). B3 note: the time-format mode select does not gate the advanced pattern
      inputs (mockup has no gating either — §4 B3 "format selects reveal dependents" left for canon).
      B3 data-gates + B9 scroll-spy reused from rd.js unchanged. Invented keys (TR/EN twins):
      ss.toastSaved/toastLangs/errValues/errLang + open./tv.(×2)/to./kf.errAttachTooBig. Tests:
      SettingsSystemCompanyTests (7) — round-trip + rerender + <title> consumer, invalid-values guard,
      maintenance flip through the page, language add/remove/primary-guard, agent-reply size cap (refuse +
      under-limit posts) — suite 192.)*
- [x] **settings-tickets.html** — all 40 switches gate for real, effort section drives B8 guards; sequence dialog CRUD (B3/B4)
      *(S7; /admin/settings-tickets (SettingsTicketsController) over ISettingsService typed sections
      (NEW TicketBehaviorSettings + the S4 EffortSettings/NumberingSettings) so the page renders exactly
      what the engine reads; every control persists (PRG toasts), server-side validation (format needs '#',
      ranges, select membership) writes NOTHING on failure. LIVE switches (13): number_format + number_mode
      (Rastgele = collision-checked random draw in TicketService.DrawNumberAsync, counter untouched;
      Ardışık = row-locked sequence — seed stays sequential, the mockup's selected "Rastgele" is sample
      state), default_status / default_priority (cascade fallback NEWLY wired in CreateAsync) / default_sla,
      default_queue_id + top_level_counts (agent TicketsController landing queue + queue-tree badges),
      max_open_per_user (end-user creates refused over the limit, staff bypass — portal open shows invented
      open.errMaxOpen; TODO(S8) overlimit mail), claim_on_response (NEW: ThreadService auto-claims on staff
      response + assigned event/TicketAssigned, osTicket auto_claim parity), require_topic_to_close (NEW
      close guard in TransitionStatusAsync → existing agent tv.errStatus toast), effort enabled /
      block_work_until_approved / mandatory_reject_note / auto_approve_threshold / revision_limit (S4 gates,
      consumed since S4), autoresp effort_proposal + alerts effort_response masters (NEW gates on
      EffortEmailHandler request/response mails). PERSISTED-ONLY (each annotated at TicketBehaviorSettings /
      the controller maps): lock_mode (IThreadService lock API exists, composer wiring pending), captcha
      TODO(S9), auto_refer_on_close (needs the referral mechanism), allow_external_images TODO(S8),
      collab_visibility (open decision #9), default_topic_id TODO(S8 inbound mail), effort unit (canon flag:
      proposals are hours everywhere) + reminder_days TODO(S8 Hangfire reminder), 5 autoresponses + 6 alert
      masters + 24 recipient boxes TODO(S8 alert fan-out); system-errors master stays checked+disabled per
      mockup (not persisted). dlg-seq = real CRUD (B4, SequenceNumberService.SaveAsync): rows become
      name/next inputs (INVENTED — mockup rows are static text, needs canon sign-off), ✕ removes, Sıra ekle
      appends via data-rule-add + NEW data-rule-into rd.js contract; guards: internal/in-use sequences
      (settings pointer, help topics) undeletable (st.errSeqInUse), Next can never move backwards — stays
      above the highest drawn number (st.errSeqNext); dialog submit drops the mockup's data-dialog-close
      (audited B2 close-swallows-save bug). B3 gating = NEW shared rd.js data-gates (effort master gates its
      dependent fields, each alert master gates its recipients row; product-only .rd-gated-off in app.css).
      B9 scroll-spy NEW in rd.js — .bo-section-index follows scroll (mockup only click-highlights), shared
      by the other 6 settings pages. Honest-defaults deviations (mockup checked = sample state, need canon
      sign-off): require_topic_to_close ships OFF (osTicket parity), sequence select shows the DB truth
      (sequential). Queues section: table = top-level SavedQueues with System pill = shared vs Custom =
      personal — the entity has no IsSystem flag yet (queues-page port adds it with delete protection);
      default-queue select offers shared queues only (the mockup's personal "SLA Riskli VIP" option is
      sample data). Invented keys (TR/EN twins): st.slaFmt/toastSaved/toastSeqSaved/errFormat/errValues/
      errSeqInUse/errSeqNext + open.errMaxOpen. Tests: SettingsTicketsTests (9) incl. the S7 exit-gate leg —
      admin HTTP POST flips block-work-until-approved → S4 guard flips for real (staff reply + close throw
      WorkBlockedByEffortException, flip back reopens) — suite 185.)*
- [x] **settings-tasks.html** — same pattern as tickets (B3/B4)
      *(S7; /admin/settings-tasks (SettingsTasksController) over ISettingsService (ns "tasks" + task_* keys
      in "alerts"; NEW TaskSettings typed section) — PRG toasts, B3 validation writes NOTHING on failure
      (tg.errFormat/errValues). LIVE (2): number_format (consumed by TaskService.CreateAsync since S6 via
      GetTaskNumberingAsync) + number_mode NEWLY consumed — TaskService.DrawNumberAsync mirrors the ticket
      draw (Rastgele = collision-checked random digits, counter untouched; Ardışık = the seeded row-locked
      task sequence). dlg-seq = the settings-tickets B4 CRUD reused (SequenceNumberService.SaveAsync with
      the same guards — in-use/internal undeletable, Next never moves backwards — through this page's own
      POST /admin/settings-tasks/sequences so the PRG lands back here; both pages edit the SAME sequence
      table). PERSISTED-ONLY (annotated at TaskSettings / the controller maps): default_priority (TaskItem
      has no priority column — osTicket keeps task priority in dynamic form data; TODO(S8) task form
      output), 5 alert masters + 13 recipient boxes as alerts/task_* keys TODO(S8 alert fan-out; seeded
      templates cover 4 of 5 masters — task.alert/task.assigned.alert/task.transfer.alert/
      task.overdue.alert; New-Activity has NO seeded template, flagged). B3 alert masters gate their
      recipients rows + B9 scroll-spy = rd.js unchanged. Honest-defaults deviations (mockup sample state,
      need canon sign-off): format input shows the DB truth "T-####" (mockup "G####"), dlg-seq shows the
      seeded rows Genel Talepler 716600 / Görev Sırası 2042 (mockup Destek 716556 / Görevler 4022).
      Invented keys (TR/EN twins): tg.toastSaved/toastSeqSaved/errFormat/errValues/errSeqInUse/errSeqNext.
      Tests: SettingsTasksKbTests (4 of 7) — round-trip + rerender, invalid-format guard, the LIVE
      numbering flip end-to-end through the admin form (random draw leaves the counter alone, sequential
      advances it), dlg-seq HTTP add/remove — suite 199.)*
- [x] **settings-agents.html** — template Edit dialogs per-row prefilled (B2); lockout policy consumed by B6
      *(S7; /admin/settings-agents (SettingsAgentsController) over ISettingsService ns "agents" (NEW
      AgentSettings typed section) — PRG toasts, B3 validation writes NOTHING on failure (sa.errValues).
      LIVE (4): max_login_attempts + lockout_minutes = the STAFF-WIDE lockout policy consumed by B6 at BOTH
      staff sign-ins (StaffAccountControllerBase.ApplyStaffLockoutAsync, password AND wrong-2FA-code paths;
      Identity's static MaxFailedAccessAttempts parked at 100 so the Setting owns the threshold across the
      3/5/10 options). LOCKOUT CANON RESOLVED toward this mockup now the page owns the values: the fields
      are staff-wide with defaults 5 attempts / 30 min (sa.maxAttempts/sa.lockDuration selected states) —
      the S7 admin-auth INVENTED admin-only 3/30 tightening is dropped (admin pwreset row updated) and
      the old agents/admin_* keys retired unread; staff-wide duration moves 15 → the mockup's 30.
      reset_window_minutes owns the staff reset-link lifespan: Program.cs seeds
      StaffResetTokenProviderOptions.TokenLifespan lazily from the Setting (config
      StaffAuth:ResetWindowMinutes = fallback only) and the page's save syncs the cached options in-process
      (single-instance assumption, flagged). allow_pwreset gates the whole staff reset flow — while off,
      GET/POST /agent/pwreset + /admin/pwreset redirect to their logins (the switch also data-gates the
      reset-window field client-side — INVENTED B3 gating, settings-kb precedent). require_twofa is real:
      StaffSignInManager.IsTwoFactorEnabledAsync ORs the policy in, so un-enrolled staff get the promised
      e-posta kodu step (EffectiveTwoFactorMethodAsync forces the Email provider when no enrollment
      exists); NOTE: an un-enrolled ADMIN under this policy satisfies AdminOnly via the email code instead
      of being routed to TOTP enrollment — flag for canon (mandatory-TOTP wording vs the mockup's e-posta
      kodu promise). Honest-defaults deviations (mockup checked/selected = sample state, need canon
      sign-off): password_policy ships "basic" (the Identity statics' floor; mockup selects "strong"),
      require_twofa ships OFF (mockup checked — defaulting on would lock every existing password-only
      staff login flow into email codes). PERSISTED-ONLY (annotated on AgentSettings): name_format /
      identity_masking / avatar_source TODO(S8 staff name+avatar rendering helpers), block_collab (TODO:
      collaborator add flow), password_policy TODO(S8 policy engine over Identity statics),
      session_timeout_minutes + ip_binding (TODO: B6 session work — staff sessions untracked, the profile
      row's precedent). B2 dialogs: the mockup's ONE dlg-tpl shared by 4 rows splits into per-row dialogs
      (canned precedent) server-prefilled via NEW ISystemTemplateService — rows live as EmailTemplate
      entries (codes staff.welcome/staff.banner/staff.pwreset/staff.2fa) in the two seeded sets, one body
      per language behind the dialog's Türkçe/English tabs, the single Ad input = shared Subject of both
      rows; reads fall back to catalog defaults (no writes on GET), saves upsert both sets (audited).
      Flag for canon: page-content rows (Giriş Bandosu) share the email-template table until a dedicated
      content mechanism exists; no variable pills (mockup shows none, dlgContentHelp text only). B9
      scroll-spy = rd.js unchanged. Invented keys (TR/EN twins): sa.toastSaved/toastTpl/errValues.
      Tests: SettingsAgentsUsersTests (5 of 8) — round-trip + rerender, invalid-values guard, lockout
      threshold/duration flipped through the page takes effect at the agent login (3 fails → locked ~15
      min), pwreset gate flip, require_twofa flip end-to-end (email-code step + code accepted, off again =
      password-only); AdminAuthTests updated to the resolved 5/30 staff-wide canon.)*
- [x] **settings-users.html** — 6 template dialogs prefilled (B2); registration mode consumed by portal
      *(S7; /admin/settings-users (SettingsUsersController) over ISettingsService ns "users" (NEW
      UserSettings typed section) — PRG toasts, B3 validation writes NOTHING on failure (su.errValues).
      LIVE (3): registration_mode gates the portal register flow HTTP-visibly — "public" self-serves,
      "closed"/"invite" 404 both GET and POST /register (settings-kb enable_kb 404 precedent) and the
      login page's "Kayıt olun" foot link disappears (ShowRegister ViewData); "invite" refuses like
      closed until an invitation mechanism exists — TODO(S8) invite tokens, flagged. max_login_attempts +
      lockout_minutes = the customer lockout policy consumed at the portal login (LoginLockoutPolicy —
      the staff twin's shared helper; customer lockout moves from Identity's 5/15 to the mockup's 5/30
      defaults). PERSISTED-ONLY (annotated on UserSettings): name_format / avatar_source TODO(S8 user
      name+avatar rendering), registration_required (TODO(S8): guest ticket-open flow does not exist —
      the switch's "misafirler yalnızca e-posta ile talep açabilir" promise needs it), password_policy
      TODO(S8 policy engine), session_timeout_minutes (TODO: B6 session work), auth_tokens (TODO(S8):
      auto-login links ride the outgoing-mail pipeline), email_verify (TODO(S8): register verification
      flow over the seeded user.confirm.email/user.verify.page/user.confirmed.page templates; honest
      default OFF — registration signs in immediately today, mockup checked = sample state, flagged).
      B2 dialogs: 6 per-row dialogs prefilled via ISystemTemplateService (codes user.access.link/
      user.banner/user.pwreset/user.verify.page/user.confirm.email/user.confirmed.page — the
      settings-agents treatment; same canon flag on page-content rows sharing the template table).
      B9 scroll-spy = rd.js unchanged; no in-page B3 master↔dependent pair exists in this mockup (the
      switches are independent). Invented keys (TR/EN twins): su.toastSaved/toastTpl/errValues.
      Tests: SettingsAgentsUsersTests (3 of 8) — round-trip, registration-mode flip gates /register +
      login link end-to-end (closed AND invite, flip back reopens), template dialog save round-trips
      shared subject + both bodies into both sets, unknown code refused — suite 207.)*
- [x] **settings-kb.html** — master switch gates portal KB visibility (B3)
      *(S7; /admin/settings-kb (SettingsKbController) over ISettingsService ns "kb" (NEW KbSettings typed
      section) — PRG toast. LIVE (2): enable_kb master switch — portal /kb + /kb-article (+vote/attachment)
      answer 404 while off (honest semantics CHOSEN: 404 over redirect, per skb.enableHelp "hidden
      entirely"; flagged for canon), the portal nav KB item (_PortalHeader) and the home KB mini-search
      card disappear with it, everything returns on re-enable; enable_canned — the agent composers
      (ticket-view reply + ticket-open first response) hide their whole canned menu and the canned insert
      endpoints refuse canned ids (deviation flagged: the ticket-view select also carries the orig/last
      quote options, which disappear with it — osTicket parity, the whole select is the canned menu).
      PERSISTED-ONLY: require_login (the whole portal — KB included — already sits behind the rd.customer
      cookie; the OFF state needs an anonymous KB route + maintenance pass-through, the settings-system
      auth_required twin — TODO, flagged). B3 in-page: the master data-gates the require_login switch
      (INVENTED gating — the mockup's static switches are independent; canned stays ungated as an
      agent-side feature). Honest default: enable_kb=true (osTicket ships KB OFF; the portal KB has been
      live since S5 and the mockup's checked state agrees — deviation noted). Invented key (TR/EN twins):
      skb.toastSaved. Tests: SettingsTasksKbTests (3 of 7) — kb round-trip, master flip through the page →
      portal routes 404 + nav/home links gone + flip back reopens, canned flip → composer menu gone +
      insert endpoint refuses — suite 199.)*
- [ ] **email-settings.html** — 16 switches gate; footer button order normalized (S0)
- [ ] **emails.html / email-edit.html** — account CRUD; IMAP/SMTP config with per-protocol dialogs (B2), protocol
      radios gate fields (B3), OAuth2 tabs, **Test connection**
- [ ] **email-diagnostic.html** — real send with pending/success/failure states (B10)
- [ ] **templates.html / template-edit.html** — set CRUD (create dialog closes properly); 21 templates each
      open their own content (B2); variable pills click-to-insert (B5); per-template preview
- [ ] **banlist.html** — add/edit/delete real; B1
- [x] **helptopics.html / helptopic-edit.html** — CRUD; number-format radio gates input; forms tab attach/detach (B3/B4)
      *(S7; /admin/helptopics + /admin/helptopic-edit?id= (HelpTopicsController; no id = create — the
      mockup's new-button links straight to helptopic-edit.html). NEW S7_HelpTopicSettings migration:
      HelpTopic.IsArchived (the editor's 3-state status select, department twin — archived implies
      inactive so the S5/S6 IsActive consumers keep working) + UseRandomNumbers (osTicket sequence_id 0).
      B1 list: tree order with children indented under their parent (departments precedent; child rows
      show the full "Bordro / Yol Ücreti" label per the mockup DOM; search flattens); DEFAULT row order =
      the toolbar's sort-mode select — Kaydet persists tickets/topic_sort_mode (manual = Sort column =
      the mockup's exact row order, alfabetik = TR collation); header sort on Konu/Güncellenme orders
      SIBLINGS; DEVIATION flagged: the mockup's static sorted-desc indicator on Güncellenme contradicts
      its own manual row order — the indicator now follows the ACTIVE sort only. dlg-more bulk
      enable/disable/delete over the selection (delete guard: topics referenced by child topics or
      tickets are skipped — teams precedent, needs canon sign-off); rd-empty = the mockup's hidden
      #ht-empty reachable. Editor: every control persists — parent select (cycle guard walks the
      ancestor chain), routing selects = REAL rows with "— Sistem varsayılanı —" as the null the S4
      CreateAsync cascade falls through (dept/initial status/priority/SLA/thank-you page); assign =
      s:{id}/t:{id} optgroups (ticket-open Ata parity); thank-you select lists ThankYou-type SitePages
      only (settings-company per-type precedent — the mockup's "Hoş Geldiniz" option is Landing-type
      sample state, flagged). B3: the custom number-format radio data-gates the format input (rd.js,
      settings-company radio precedent) and the server refuses a '#'-less custom format (hte.errFormat);
      the sequence select = seeded Sequence rows joined onto the mockup's two options — empty "Genel
      sıra" = the global numbering settings, "Rastgele" = the NEW per-topic UseRandomNumbers, honored by
      TicketService.DrawNumberAsync (tested: an admin-edited format is drawn on the next ticket; random
      leaves the global counter untouched); sequence CRUD stays on settings-tickets. Forms tab (B4):
      one block per attached HelpTopicForm — head "Ek form: {0}" (invented hte.formsHeadFmt; the
      mockup's hte.formsHead hardcodes "Talep Detayları" — its 4 sample rows are the BUILT-IN ticket
      form while the seed attaches Bordro Ek Bilgileri, sample-state conflict flagged), per-field enable
      checkboxes persisted as the osTicket-parity Extra {"disable":[ids]} which BOTH open pages now
      honor (portal open + agent ticket-open field scopes; tested end-to-end: a disabled field
      disappears from /open); attach = "Form ekle" clones a per-form <template> client-side (page
      script, portal-open precedent — the mockup button is dead), detach = INVENTED ✕ per block (the
      ROADMAP row asks for detach, the mockup defines no control — needs canon sign-off); the add
      select lists General-kind definitions only (the mockup's "Şirket Bilgileri" option is the builtin
      org form — sample state, flagged); everything reconciles in the ONE page save. Editor delete =
      dlg-delete confirm → topics referenced by tickets/children are REFUSED (hte.errInUse — mockup has
      no reassign flow; archive instead, flagged). Invented keys (TR/EN twins): ht.dateFmt/archived/
      bulk*/toast* + parameterized ht.showing; hte.newTitle/err*/formsHeadFmt/tChoice/tDate/addFormPh/
      detachTitle. Differ notes: Updated cells show live timestamps (seed carries no canon dates);
      priority/status selects list ALL real rows vs the mockup's samples. +7 tests
      (HelpTopicsSlasAdminTests).)*
- [x] **filters.html / filter-edit.html** — CRUD; rule/action rows add/remove (B4); match-count preview; filters actually run in the mail/ticket pipeline
      *(S7; /admin/filters + /admin/filter-edit?id= (FiltersController; no id = create — the mockup's
      new-button links straight to filter-edit.html). NEW S7_Filters migration: Ticket.AutoResponseDisabled
      (the noautoresp action's persisted per-ticket flag — consumed at autoresponse send, TODO(S8)); the
      filter/filter_rule/filter_action tables are S3 stock. SEED CANON ALIGNED: the VIP filter now carries
      the filter-edit mockup's exact editor state (match-ALL + stop-on-match ON, rules org=Tosyalı Holding /
      email⊃@tosyali.com / subject⊃acil, actions priority high → dept Bordro → SLA VIP, the notes text) —
      it previously held different S3 sample rules (2× email endswith + subject, single action; list-page
      rule counts unchanged). B1 list: sort name/order/updated, DEFAULT = exec order ASC (the mockup's
      static sorted-desc indicator contradicts its own 1→4 rows — indicator follows the ACTIVE sort,
      helptopics deviation precedent), search, pagination (PageSize 8), dlg-more bulk enable/disable/delete
      (no in-use guard — nothing references filters), rd-empty = the mockup's hidden #fl-empty reachable;
      target cells show the mailbox address for email_id-restricted filters, invented fl.targetAny for
      channel-less ones. Editor: whole-page save (B3 refuses writing NOTHING on bad rows); target select =
      the 4 mockup channels + REAL EmailAccount rows in the optgroup (persisted as Target+EmailAccountId,
      osTicket email_id; the seed's third mailbox bilgi@ appears — real-rows precedent); rule rows (B4,
      data-rule-add clone) = the mockup's 6 fields × 4 operators — rows holding other osTicket tokens (seed
      what "source", the spam filter's "ends" op) render as verbatim appended options (schedules-timezone
      precedent, flagged); duplicate rules refused (unique index), regex rules must compile, ≥1 rule
      enforced (osTicket parity); action rows = the mockup's 11 types + the seed's verbatim "note" — the
      mockup only shows priority/dept/sla param selects, the other types get their real-data select or a
      text input (INVENTED per B4, needs canon sign-off); one hidden actValues per row keeps the posted
      arrays aligned (page script mirrors the visible param control); rows reconcile by hidden id. Create
      defaults: stop-on-match OFF + target Any (the mockup's checked switch/email target = VIP sample
      state, flagged), exec order = max+1. ENGINE (the row's bold leg): FilterEngine (Infrastructure;
      matcher is pure static) runs INSIDE TicketService.CreateAsync for every create channel that exists
      today — portal open (Web), agent ticket-open (recorded web/email/other/phone; phone/other only meet
      target "Any"), direct service creates; the MAIL pipeline is TODO(S8) and will feed Source=Email +
      ReplyTo + the receiving mailbox id. Exec order, match-all/any and stop-on-match honored; reject
      halts the run. Matching is ordinal case-insensitive (Turkish İ/ı not culture-folded — documented;
      regex verbatim with 250ms timeout, an invalid pattern matches NOTHING on either polarity). LIVE
      actions: reject (typed TicketRejectedByFilterException → portal open.errFiltered / agent
      to.errFiltered form errors; the filter name never leaks to end users; NOTE: an agent-side
      inline-created guest user row survives the refusal — flagged), dept, priority, sla (grace due date
      recomputed), topic (re-cascades the new topic's routing), status (a non-open status = honest
      auto-close, ClosedAt stamped at birth), team, agent (vacation guard applies), noautoresp (persisted
      flag), note (SYSTEM internal note on the fresh thread). PERSISTED-ONLY: canned + email actions
      TODO(S8 outbound mail). PREVIEW (B4 "N tickets would match" — INVENTED control, the mockup defines
      none; needs canon sign-off): a rules-tab button POSTs the CURRENT unsaved rules to
      /admin/filter-edit/preview; rules are evaluated over the whole ticket store REGARDLESS of target
      channel (the preview tests the rules; the channel gate only applies to live traffic — documented);
      mail-only fields (reply-to) cannot match stored tickets — skipped from the count + reported via
      fle.previewMailOnly; body rules read the thread's first Message. Invented keys (TR/EN twins):
      fl.targetAny/dateFmt/bulkNone/bulkDone/bulkPartial/toastCreated/toastSaved/toastDeleted +
      re-parameterized fl.showing; fle.newTitle/errName/errOrder/errValues/errRule/errRules/errDupRule/
      errRegex/errAction/preview/previewResult/previewMailOnly/previewError; open.errFiltered/
      to.errFiltered. Differ notes: Updated cells show live timestamps (seed carries no canon dates);
      param selects list ALL real rows vs the mockup's samples. +10 tests (FiltersAdminTests) — suite 265.)*
- [x] **queues.html** — full builder: criteria/columns/sort/conditions rows (B4), drag-reorder columns, export
      column set, **live preview**, saved queue appears in agent queue tree
      *(S7; /admin/queues (QueuesController): ?id=N edits a SHARED queue, id=0 = create, no id lands on the
      first shared root so the page opens filled like the mockup. Pure EDITOR over the existing engine — no
      parallel model. Criteria rows (B4) ↔ the flat v1 criteria JSON QueueEngine executes: "is" on
      status/priority/dept/assignee writes the executed keys (dept as a typed id; assignee offers
      me/my-teams/none + real staff), slaRemaining/orgTag write the SEEDED descriptive spellings
      sla_remaining_lt(_gt)/org_tag the engine logs-and-ignores by design, other operators suffix the key
      (org_tag_contains…); JSON keys outside the mockup's six fields (state/isanswered/isoverdue/closed/
      effort/topic…) render as VERBATIM rows with typed write-back so the seeded canon tree round-trips
      loss-free (schedules/filters verbatim-option precedent). Columns tab = SavedQueueColumn rows (heading
      override, width, "auto" persists as 0) — drag-reorder via the NEW shared rd.js
      data-drag-rows/data-drag-handle HTML5-DnD contract (INVENTED, mockups promise ⋮⋮ but ship no JS; the
      S7 form/list builders reuse it); adding needs an INVENTED column-picker select beside the mockup's
      bare button (a row must name a QueueColumn def — filter-edit action-pick precedent). Sort tab composes
      ONE default QueueSortOption ("field"/"-field" JSON list, reused when an identical option exists;
      TicketListEngine.SortFromOptionColumns GENERALIZED from string literals to parse any such list — first
      mappable entry wins, bare priority__urgency = most urgent first); the inherit switch clears the
      queue's own rows (true parent-sort inheritance TODO — agent falls back to updated-desc). Conditions
      tab persists osTicket-parity row-styling JSON in NEW SavedQueue.Conditions — NO consumer yet (TODO:
      agent list renderer, flagged). Export tab → SavedQueueExportField rows (canonical TR headings); the S6
      agent CSV export now HONORS the active queue's set (explicit cols param still overrides). Live
      preview (B4): the Preview tab POSTs the CURRENT unsaved form to /admin/queues/preview → real
      QueueEngine (admin actor) + ApplySort → HTML fragment rendered with the posted column headings
      (default = the mockup's five pv columns); the GET renders the same fragment server-side. NEW
      S7_QueueBuilder migration: SavedQueue.IsSystem (the 12 seeded shared queues; delete guard qb.errSystem;
      the settings-tickets System/Custom pill now reads the real flag) + SavedQueue.Conditions. Delete also
      refuses queues with children (qb.errChildren); the parent select excludes self+descendants
      (materialized-path cycle guard; reparenting rewrites subtree paths); personal queues 404 in the
      builder — admin manages the SHARED tree only (flagged). INVENTED page-head actions (queue picker
      select + Yeni Kuyruk + Sil w/ confirm dialog — the mockup shows one filled builder with no switcher;
      filter-edit precedent, needs canon sign-off). Honest create defaults (the mockup's rows are the sample
      queue's data): zero criteria/column/sort/condition rows, export = the mockup's 9 checked boxes.
      Bonus: the agent tickets "SLA Kalan" cell upgraded from due-date text to the countdown chip parked as
      TODO(S7) on the tickets row — "0s 45dk" per this mockup's preview canon (tq.slaFmt/qb.slaFmt;
      thresholds INVENTED from its samples: danger <60 dk or overdue, warn <90 dk — needs canon sign-off).
      Invented keys (TR/EN twins): qb.pickLabel/new/pickColumn/aMe/aMyTeams/aNone/toastSaved/toastCreated/
      toastDeleted/errName/errParent/errDupCol/errSystem/errChildren/previewEmpty/previewError/slaFmt +
      tq.slaFmt. Tests: QueuesAdminTests (7) — two-way mapping round-trips (incl. the seeded SLA VIP JSON),
      whole-page save + column drag order persistence + reconciliation, preview executes UNSAVED state
      through the engine, agent CSV honors the export set, IsSystem/children delete guards + cycle guard —
      suite 272. E2E gate leg 2 GREEN: QueueBuilderTests, see the §5 S7 line.)*
- [x] **forms.html / form-edit.html** — form designer: field CRUD, per-field config dialog writes back (B2),
      type select reveals options editor, drag-reorder, live preview; output consumed by portal/agent open pages
      *(S7; /admin/forms + /admin/form-edit?id= (FormsController; no id = create). Pure EDITOR over the S3
      FormDefinition/FormField model both open pages already consume through HelpTopicForm — no parallel
      model. List: built-in (IsSystem) vs custom tables split per the mockup, shared name/updated header
      sort, search over both, custom-side pagination + reachable rd-empty, dlg-more bulk enable/disable/
      delete (system rows guard-skipped; NEW FormDefinition.IsActive via migration S7_FormsLists — disabled
      forms drop out of the helptopic-edit attach list except already-attached, schedules IsActive
      precedent; delete removes the entered FormEntry data WITH the form per the fme.deleteWarn canon).
      Editor: whole-page save (B3 refuses writing NOTHING on bad title/blanked labels); field rows = the
      mockup DOM + hidden fieldIds + a fieldMarks "row"/"req"/"int" marker sequence (schedules precedent) —
      posted DOM order IS Sort (tbody = the shared rd.js data-drag-rows/data-drag-handle contract from the
      queues port); type mapping fme.tShort…tFile ↔ text/memo/choices/date/checkbox/phone/file (checkbox/
      phone/file degrade to text inputs on the open pages — their default branch, flagged). B2 ⚙ dialog:
      ONE dialog parameterized by the opening row — hint/default/validation live in per-row hidden inputs,
      ⚙ copies in, Uygula writes back (rows are client-created, so per-row server dialogs cannot exist;
      write-back IS the parameterization). B3 options editor: choosing "Seçim" reveals an INVENTED
      list-select + one-option-per-line textarea under the type select (schedule holiday-mode reveal
      precedent; needs canon sign-off) → Configuration {"list_id":N} (real ListDefinitions; inactive hidden
      except current) or {"choices":[…]}; NEW shared Domain FormFieldConfig owns the JSON (list_id/choices/
      default/validation) and BOTH open controllers now consume it: inline choices render as options and
      persist as Value-only FormEntryValues, ⚙ defaults pre-fill untouched controls, ⚙ validation
      (email/phone/number) is enforced server-side at both submits (invented open.errFieldFormat/
      to.errFieldFormat). Variable names auto-slug TR-aware from the label when blank, uniqued in-form.
      Field delete honesty (osTicket parity): a removed row with existing answers is soft-disabled
      (IsDisabled; FormEntryValue→FormField is Restrict) — toast fme.toastSavedDisabled reports the count,
      fme.disabledInfo surfaces archived fields on the editor (no mockup UI to re-enable them, flagged);
      answerless rows hard-delete. Live preview (B4): INVENTED head button + dialog (queues/filters
      precedent, needs canon sign-off) POSTs the CURRENT unsaved rows to /admin/form-edit/preview →
      fragment rendered as portal /open would (user-visible fields only; internal rows excluded).
      Seed aligned to the mockup canon: Bordro Ek Bilgileri now carries the mockup's four rows (Personel
      Numarası req + ⚙ hint sample / Dönem req / Modül choices / Ek Açıklama memo internal — was 3
      differently-labelled fields). Instructions persist but neither open page renders them yet
      (fme.instructionsHelp promise — TODO, flagged). Mockup's "required" checkbox maps to BOTH
      RequiredForUsers+ForAgents (internal fields agent-only). Latent bug found while porting: parameterized
      toasts composed via LocalizedHtmlString.Value render UNFORMATTED ({0} braces) — these pages use
      L.GetString(...); the same pattern exists in earlier ports (schedules/slas/helptopics/filters bulk
      toasts), flagged for a sweep — SWEPT 2026-08-19: all 16 parameterized L[...].Value usages across
      admin+agent views (incl. S6 tickets/tasks/canned/users bulk toasts and the sla/team dialog titles)
      replaced with L.GetString(...). Invented keys (TR/EN twins): fm.dateFmt/bulkNone/bulkDone/bulkPartial/
      toastDeleted + re-parameterized fm.showing; fme.newTitle/errTitle/errLabel/errSystem/toastSaved/
      toastCreated/toastSavedDisabled/disabledInfo/optListTitle/optCustom/optChoicesPh/optChoicesTitle/
      preview/previewHelp/previewEmpty/previewError. +6 tests (FormsListsAdminTests) incl. the GATE PROOF
      leg — see the §5 S7 line.)*
- [x] **lists.html / list-edit.html** — list editor: item CRUD + reorder (sort-mode gates), import dialog works,
      properties fields; system lists protected (B4)
      *(S7; /admin/lists + /admin/list-edit?id= (ListsController; no id = create). B1 list over
      ListDefinition rows: name/created/updated header sort (updated-desc default), search (name + plural),
      pagination + reachable rd-empty; display name = PluralName ?? Name (the mockup rows spell "Modüller").
      System lists (Type != null — the seeded status/priority mirrors) protected EVERYWHERE per the mockup:
      disabled selection checkboxes, guard-skipped by every bulk action, item counts mirrored LIVE from the
      real status/priority tables (canon 7/4 over empty mirror rows), read-only editor (disabled controls +
      invented lse.systemNote banner) whose item rows render the real TicketStatus/TicketPriority rows, and
      save/delete/import refuse server-side (lse.errSystem). dlg-more bulk enable/disable/delete (NEW
      ListDefinition.IsActive, same S7_FormsLists migration — inactive lists hide from the form designer's
      choice-list select except a field's current one; delete guard-skips lists still referenced by a
      choices field's list_id, teams precedent). Editor: whole-page save (B3 refuses on blank name, blanked
      existing item values lse.errItem, TR-case-insensitive duplicate values lse.errDupItem); item rows =
      mockup DOM + hidden itemIds + itemMarks "row"/"on" markers for the enabled switch; B4 sort-mode
      gating honest on BOTH sides: the mockup has NO drag handles on items — reorder is the Sıra number
      column, and the sort select gates it via the shared rd.js select data-gates contract (manual enables;
      the alpha modes disable the inputs — which then don't post — and the server re-orders by TR collation
      on save, ascending/descending). "＋ Öğe Ekle" appends a template row carrying the staged input value
      (client clone, then re-fires the gate sync). dlg-import = a REAL POST (mockup's data-dialog-close on
      the submit dropped — the audited B2 close-swallows-save bug): value[,abbrev] per line, honest report
      lse.importResult "{N} added, {M} skipped" (blank lines ignored; duplicates of existing values and
      in-paste repeats skipped); disabled on create (the list must exist — schedules clone precedent);
      alpha lists re-order after import. Properties tab = the per-item field designer persisted as the
      osTicket-parity Configuration JSON {"properties":[{label,type,name,required,internal,help?,default?,
      validation?}]} — rows drag-reorder (data-drag-rows), ⚙ dialog write-back = the form-edit B2 twin,
      names auto-slugged/uniqued; seed's sorumlu_ekip normalized to the designer vocabulary ("choice"→
      "choices") + the mockup's checked Dahili flag. HONESTY FLAGS: per-item property VALUES have no
      editing UI anywhere in the mockups (ListItem.Properties stays untouched — TODO when canon adds an
      item dialog); the properties designer's "Seçim" type carries no options editor (the mockup's props
      rows show none); historic answers keep their copied Value string when an item is deleted
      (FormEntryValue.ValueId is a soft reference by design). Invented keys (TR/EN twins): ls.dateFmt/
      bulkNone/bulkDone/bulkPartial/toastDeleted + re-parameterized ls.showing; lse.newTitle/errName/
      errItem/errDupItem/errSystem/errInUse/toastSaved/toastCreated/importResult/systemNote. +7 tests
      (FormsListsAdminTests) — suite 285.)*
- [x] **slas.html** — per-row dialog prefilled (B2); grace/transient switches consumed by SLA engine
      *(S7; /admin/slas (SlasController). B1: header sort name/grace/updated (updated desc default =
      the mockup's indicator AND its exact row order Standart → Dahili Talepler via the id tiebreak),
      search, pagination (PageSize 8), dlg-more bulk enable/disable/delete (delete guard: plans
      referenced by tickets/departments/help topics or the core default_sla_id pointer are skipped —
      teams precedent, flagged); rd-empty = the mockup's hidden #sla-empty reachable. B2: the mockup's
      ONE hardcoded dlg-sla split into a create dialog + per-row dialogs prefilled server-side (name,
      grace, status radios, schedule select = BusinessHours schedules only — holiday calendars are no
      SLA clock, flagged; transient + alerts switches, notes; per-dialog titles sla.dlgTitleFmt/
      dlgNewTitle invented, teams precedent); submit drops the mockup's data-dialog-close (the audited
      B2 close-swallows-save bug). ENGINE CONSUMPTION — grace: TicketService.CreateAsync now stamps
      EstimatedDueDate = create + GracePeriodHours whenever an SLA lands on the ticket, so the value
      every existing due/overdue consumer reads (DueDate ?? EstimatedDueDate: live board chips, agent
      list due sort, dashboards, ticket view) follows the admin-edited grace (tested end-to-end:
      dialog-created 6h plan → ticket due ≈ +6h); wall-clock hours for now — TODO(S8): the SLA sweep
      recomputes schedule-aware (the clock only runs inside the plan's working schedule) and flags
      IsOverdue/escalation. Transient: TicketService.TransferAsync re-resolves a transient plan (topic
      SLA → new department SLA → core default) and recomputes the due date under the replacement
      (osTicket FLAG_TRANSIENT semantics; tested: transfer to Bordro swaps the transient plan for
      Standart); topic changes have no edit path yet — the transfer leg is the wired one. Overdue-alerts
      switch persists as the inverted DisableOverdueAlerts TODO(S8 sweep alert fan-out);
      EscalateOnOverdue has no mockup control (osTicket flag kept on the entity, untouched). Invented
      keys (TR/EN twins): sla.dateFmt/dlgNewTitle/dlgTitleFmt/err*/toast*/bulk* + parameterized
      sla.showing. Differ note: Updated cells show live timestamps (seed carries no canon dates).
      +5 tests (HelpTopicsSlasAdminTests) — suite 247.)*
- [x] **schedules.html / schedule-edit.html** — entry/holiday rows add/remove (B4); timezone; **diagnostic answers**; Clone works
      *(S7; /admin/schedules + /admin/schedule-edit?id= (SchedulesController; no id = create — the mockup's
      new-button links straight to schedule-edit.html). NEW S7_ScheduleEntries migration:
      ScheduleEntry.IsHoliday (entry vs holiday rows share the one osTicket-parity schedule_entry table;
      legacy pre-column rows classified by shape — date-only one-time = holiday) + Schedule.IsActive
      (DB default TRUE; the bulk dialog's Etkinleştir/Devre Dışı Bırak needed a column — the mockup list
      shows NO status column, so state is visible only through pickers: the SLA dialog, settings-system
      default-schedule and department-edit selects now hide inactive schedules except a row's own current
      selection). B1 list: sort name/created/updated (updated-desc indicator + id tiebreak = the mockup's
      exact row order — its sample dates contradict their own sorted-desc, helptopics deviation precedent),
      search, pagination (PageSize 8), dlg-more bulk with delete guard (SLA plans / departments /
      system default_schedule_id skip+partial toast; NOTE: the row's "topic references" don't exist —
      HelpTopic carries no schedule column); rd-empty = the mockup's hidden #sch-empty reachable. Editor:
      whole-page save (B3 refuses writing NOTHING on bad rows); entry rows (B4, data-rule-add/-into +
      tr.bo-rule-row ✕, settings-tickets dlg-seq contract) post weekday checkboxes as an entryDayMarks
      "row"/bit marker sequence (staff-edit precedent; bits Sun=1…Sat=64, seed Day=62 parity); INVENTED
      per-row anchor-date input revealed for Aylık/Tek seferlik repeats (the mockup row cannot express
      them — monthly = anchor's day-of-month, once = the anchor date; needs canon sign-off); holiday rows
      = date+name+mode with an INVENTED gated time pair for "Saat aralığı" (all-day otherwise; needs canon
      sign-off; no yearly-repeat control — mockup defines none, osTicket repeats-yearly parity gap
      flagged); rows reconcile by hidden id (update-in-place/remove/add). SAMPLE-STATE CONFLICT flagged:
      the mockup edits "Hafta içi" yet shows 29 Ekim/1 Ocak holiday rows — seed canon keeps those on
      Resmi Tatiller 2026 (renders them in its Tatiller tab); Hafta içi's holiday tab is honestly empty.
      Timezone: the mockup's 3 fixed options (+ system default = system/timezone Setting fallback) with
      real IANA values; a schedule holding another valid id renders appended verbatim; Berlin's "(UTC+01)"
      label kept verbatim (DST mismatch in the mockup label, flagged). Clone = deep copy (schedule + every
      entry/holiday, Sort/StopsOn/Week/Month included) named via view-supplied sche.cloneNameFmt
      "{0} (Kopya)" + numeric uniqueness suffix, lands on the copy's editor; clone/delete disabled on
      create. DIAGNOSTIC (B10): dlg-diag POSTs the CURRENT editor state (unsaved rows included — works on
      the create form) + date to /admin/schedule-edit/diagnose; Domain ScheduleEvaluator merges entry
      ranges and subtracts holidays (all-day clears the date, timed rows cut their span); JSON
      status/ranges/holiday composed client-side from data-fmt-* resx attributes — sche.diagResult
      re-parameterized to "…: {0}" (mockup hardcodes 09:00–18:00); holiday-kind schedules answer the
      holiday question (invented diagHoliday/diagNoHoliday); diag date defaults to TODAY (mockup's
      2026-08-12 = sample state). The date answer is wall-clock (tz-free); the tz-aware instant API
      ScheduleEvaluator.IsOpenAt (open in Istanbul ≠ open under UTC for the same instant) is the S8 SLA
      sweep's hook, tested. osTicket Yearly repeats (enum kept) are inexpressible in the select — render
      as "once", flagged. Invented keys (TR/EN twins): sch.dateFmt/toast*/bulk* + parameterized
      sch.showing; sche.newTitle/toastCloned/cloneNameFmt/entryDateTitle/diagClosed/diagHoliday/
      diagNoHoliday/diagError/err*. +8 tests (SchedulesAdminTests) — suite 255.)*
- [ ] **pages.html** — site page CRUD (B2); pages served on portal
- [ ] **apikeys.html** — key CRUD + regenerate + copy-to-clipboard; IP restriction enforced by the API (B2)
- [ ] **plugins.html** — feature-flag install/enable/disable per module; per-module configure entry (§3 replaced)
- [x] **staff.html / staff-edit.html** — CRUD; permission cards master↔children (B3); access/team rows (B4);
      LDAP/auth-backend select gates fields; password dialog validated (B2) *(S7; /admin/staff +
      /admin/staff-edit?id= (StaffController; no id = create — the mockup's new-button links straight to
      staff-edit.html). CRUD = the Identity+domain PAIR: create posts also create the StaffUser (Agent role,
      +Admin when flagged, FullName claim); edits sync username/email/full-name/admin-role to Identity;
      delete removes both. İzinler cards = per-staff permission OVERRIDE, osTicket staff.permissions parity:
      NEW S7_StaffPermissions migration adds Staff.Permissions (null = inherit role — the matrix prefills
      from the primary role), Staff.AuthBackend, Staff.RequirePasswordChange, Staff.UsePrimaryRoleOnAssigned.
      PermissionService: a non-null override REPLACES the role's grants for the 24 PermissionKeys.
      StaffOverridable matrix keys in EVERY accessible department (the mockup matrix is agent-global —
      needs canon sign-off); keys outside the staff matrix stay role-driven; a matrix saved equal to the
      role's grants stores null so the staff keeps inheriting role edits. Masters = rd.js data-check-master
      tri-state (role-edit contract). Auth backend: NEW rd.js select gating (select[data-gates] +
      data-gates-on) — "LDAP / Active Directory" disables Parola Belirle client-side, the server refuses
      too; ldap persists with TODO(S9 SSO): sign-in still verifies the local Identity password (flagged).
      Password dialog (B2): edit = its own form → Identity reset-token path (no old password; ≥8 chars with
      letter+digit mirroring rd.js data-pw-strength/-match — settings-agents password_policy persists but
      still has no engine, flagged); create = the dialog rides INSIDE the create form (the mockup nests a
      dead form outside — flagged) and a local-backend create requires the initial password. dlgPwChange =
      RequirePasswordChange: sign-in redirects to /agent/profile whose own password change clears it (nudge,
      not a hard wall — flagged); "Hesap kilitli" = Staff.IsActive=false with a NEW login gate (locked staff
      cannot sign in, lockedOut error). 2FA pill live (app/email/off; se.twofaOff/twofaEmail invented);
      Sıfırla drops the enrollment via a hidden carrier form (mockup button dead; type=submit + form attr —
      flagged) — admins re-enroll through /admin/2fa-setup on next login. Erişim (B4): primary dept/role
      selects + UsePrimaryRoleOnAssigned (persist-only, TODO with assigned-ticket permission resolution);
      extended rows = StaffDepartmentAccess from the staff side as data-rule-add rows — accessDepts/
      accessRoles parallel arrays, alerts via an INVENTED row/on marker sequence (unchecked checkboxes post
      nothing); primary dept dropped server-side. Takımlar (B4): TeamMember roster from the staff side
      (data-roster contract, teams-port mirror). B1 list: sort name (desc default per the mockup
      indicator)/username/status/dept/role/lastLogin; search name/username/email; dept+team filter selects
      with the mockup's Uygula; status pills locked(overdue)/vacation(wait)/active(solved); bulk
      Etkinleştir/Kilitle/Sil with INVENTED guards (self and the last active admin are skipped; delete
      skips staff referenced by tickets/tasks/thread entries/dept manager/team lead — teams precedent,
      needs canon sign-off) + invented confirm dialog; Dışa Aktar = filtered-list CSV UTF-8 BOM; rd-empty =
      the mockup's hidden #empty-staff reachable. Seed: uakin.Notes = the mockup's Notlar text; staff-edit's
      other uakin sample state (VIP Masası membership, Destek/Danışmanlık extended rows, task.delete off)
      contradicts the teams/departments seed canon — NOT reseeded, needs canon sign-off. Invented keys
      (TR/EN twins): sf.dateFmt/bulk*/deleteConfirm/toastSaved/toastCreated; se.newTitle/err*/toastPassword/
      toast2faReset/twofaOff/twofaEmail. +10 tests (StaffAdminTests) — suite 235.)*
- [x] **teams.html** — per-row dialog prefilled; member roster add/remove (B2/B4) *(S7; /admin/teams
      (TeamsController). B1: header sort name/status/lead/updated (name desc default = the mockup's
      sorted-desc indicator), search, pagination (PageSize 8, canned precedent), bulk Etkinleştir/Devre
      Dışı Bırak/Sil over the selection with an invented confirm dialog; rd-empty = the mockup's hidden
      #empty-teams made reachable. B2: the mockup's ONE hardcoded dlg-team split into a create dialog +
      per-row dialogs prefilled server-side (name, status radios, lead, no-alerts switch, notes, member
      rows incl. per-member Uyarılar state — seed's dkaya renders unchecked). B4 roster = NEW rd.js
      data-roster contract: Ekle clones the dialog's <template>, fills staff id/name/initials and hides
      the chosen option; the row ✕ (shared .bo-rule-row .remove) restores it; the save reconciles
      memberIds/alertIds server-side (add/remove/alert flip in one post, dedup + real-Staff validation).
      Delete guard (flagged choice): teams referenced by tickets, tasks or help-topic routing are skipped
      in the partial toast; thread-event history is a snapshot and does not block. INVENTED (needs canon
      sign-off): empty lead option "—" (tm.leadNone — dlgLeadHelp says optional but the mockup select has
      no empty option); per-dialog titles tm.dlgTitleFmt "Takım Düzenle — {0}" / tm.dlgNewTitle (the
      mockup hardcodes Bordro Ekibi in tm.dlgTitle). Invented keys (TR/EN twins): tm.dateFmt,
      toastSaved/Created, bulkNone/Done/Partial, deleteConfirm, errName/errNameInUse. Lead cell shows the
      staff row's full name (seed "Ümit Yaşar Akın") vs the mockup's short-form sample "Ümit Y. Akın" —
      dashboard full-name precedent. +3 tests (RolesTeamsAdminTests).)*
- [x] **roles.html / role-edit.html** — 42-box matrix with tri-state masters (B3); role consumed by authz on every endpoint
      *(S7; /admin/roles + /admin/role-edit?id= (RolesController; no id = create — the mockup's Yeni Rol
      button links straight to role-edit.html). Matrix = 36 child boxes + 6 group masters (the row's 42):
      every box maps 1:1 onto the REAL seeded PermissionKeys the S3/S4 PermissionService already enforces —
      tickets group carries effort.propose (Efor Öner) and thread.edit (Konuşma Düzenle); users/orgs/kb/misc
      per PermissionKeys. 4 keys NEWLY ADDED for canon parity (user.create, user.delete, org.create,
      org.delete): persisted by the matrix + seeded (Yönetici all, Kıdemli minus deletes) but NOT yet
      consumed — UserService/OrgService still gate create/delete on user.edit/user.manage/org.edit
      (flagged TODO with the role rollout across endpoints); 0 mockup boxes left unmapped. DomainSeeder
      Kıdemli realigned to the canon matrix (thread.edit now off, banlist.manage now on — both previously
      contradicted role-edit.html; no consumer existed for either). B3 tri-state: NEW rd.js
      data-check-master (master toggles its group; child changes sync the master to
      checked/indeterminate/unchecked; masters post nothing) + app.css :indeterminate half-track
      (INVENTED look — the mockup defines none); server drops unknown/duplicate keys, so matrix integrity
      holds regardless of client state. Propagation: PermissionService is scoped with a per-request memo
      over Role.Permissions — a saved matrix flips authz on the NEXT request with no cache to invalidate
      (tested end-to-end: revoking canned.manage refuses CannedResponseService.Create for a holder).
      B1 list: sort name (desc default per the mockup indicator)/status/updated; live "Temsilci Sayısı" =
      distinct staff holding the role primary OR via dept-access; search; pagination; bulk
      enable/disable/delete with in-use guard (role held by any staff is skipped) + invented confirm
      dialog; rd-empty = the mockup's hidden #empty-roles reachable. The editor has NO status control
      (mockup parity) — IsEnabled moves only via the list bulk buttons. Invented keys (TR/EN twins):
      re.newTitle/errName/errNameInUse; rl.dateFmt/toastSaved/toastCreated/bulkNone/Done/Partial/
      deleteConfirm. +5 tests (RolesTeamsAdminTests) — suite 215.)*
- [x] **departments.html / department-edit.html** — CRUD with hierarchy (collapse); autoresponder switches gate;
      access rows (B3/B4); export *(S7; /admin/departments + /admin/department-edit?id=. Hierarchy: the S3
      ParentId + materialized Path carry the tree; the list renders tree order (children indented with the
      mockup's └ DOM, sort key orders SIBLINGS — default name desc = the mockup's exact row order; search
      flattens), collapse = NEW rd.js data-tree-toggle caret on parent rows (INVENTED look, app.css
      .rd-tree-toggle — the mockup only indents; needs canon sign-off). Save guards self/descendant parents
      (Path cycle check) and rewrites the whole subtree's paths on reparent; name unique per parent. NEW
      S7_DeptSettings migration: Department.IsActive (3-state status select: aktif/devre dışı/arşivlenmiş),
      AssignPrimaryOnly (3-mode atama), DisableAutoClaim, DisableReopenAutoAssign, AlertGroup enum +
      Staff.PrimaryDepartmentAlerts. Honest flag wiring: DisableAutoClaim gates the global
      claim_on_response in ThreadService NOW; DisableReopenAutoAssign clears the assignment on reopen in
      TicketService NOW (mockup help canon: "son atanan temsilciye otomatik verilmez");
      TicketAutoResponse/MessageAutoResponse (the "kapat" switches are inverted flags),
      AutoResponseEmailAccountId and AlertGroup persist with TODO(S8) markers — no mail fan-out exists yet.
      Routing consumers still check only IsArchived (disabled-dept routing guard deferred, flagged).
      Erişim tab (B4, data-roster contract extended with [data-roster-remove] for <tr> rows): rows = primary
      members (Staff.DepartmentId, "Birincil" badge — the role select edits their PRIMARY RoleId, alerts →
      Staff.PrimaryDepartmentAlerts; the row's ✕ is refused server-side, restored + partial toast — mockup
      shows ✕ on the primary row too, needs canon sign-off) + StaffDepartmentAccess rows whose per-dept role
      the PermissionService resolves on the next request (tested end-to-end: an access row grants
      ticket.reply in THAT dept only, removal revokes); memberIds/memberRoles post as DOM-order parallel
      arrays; new-row role default = first option (Yönetici, mockup defines none — flagged). Seed canon
      realigned to department-edit: Bordro AssignMembersOnly + TR template set + bordro@ AR address + dkaya
      extended access (Temsilci, alerts off — TaskServiceTests' "dkaya only sees Destek" premise updated to
      Danışmanlık). List agent counts are live (primary + extended distinct: Destek 3/Bordro 3/Danışmanlık 1
      vs the mockup's static 2/1/1 — differ note, seed roster canon wins). Bulk delete guard-skips depts
      referenced by children/staff/tickets/tasks/topics/canned (mockup has NO reassign dialog — osTicket
      asks for a target dept; guard-skip per teams precedent, needs canon sign-off) + invented confirm
      dialog; rd-empty = the mockup's hidden #empty-departments reachable. Export = the Erişim tab's mockup
      button → member CSV UTF-8 BOM (name/birincil/rol/uyarılar; the LIST has no export control — the row's
      "export" read as the members export). Invented keys (TR/EN twins): dp.archived/collapseTitle/bulk*/
      deleteConfirm/toastSaved/toastCreated/toastPrimaryKept; de.newTitle/errName/errNameInUse/errParent/
      errParentCycle. +10 tests (DepartmentsAdminTests) — suite 225.)*

## 7. Open decisions (carry-over, timeline questions removed)

1. ~~Single-tenant or multi-tenant?~~ — **DECIDED 2026-08-13: single-tenant.** S3 domain modelling is unblocked; no tenancy column/filter anywhere.
2. Hosting: Linux containers (recommended) vs Windows/IIS; cloud vs on-prem.
3. Integrations: SSO (Entra ID/LDAP), CRM/ERP, WhatsApp/telephony, billing on approved effort hours.
4. Legacy migration source (live osTicket data?) — shapes S9.
5. Languages beyond TR+EN.
6. PostgreSQL confirmed, or mandated SQL Server?
7. Who operates production; uptime/backup SLA; DNS control for SPF/DKIM; KVKK residency/retention.
8. UAT owners and acceptance criteria; single decision-maker for design disputes.
9. Portal ticket scope: own tickets only (current QueueEngine behavior; matches every mockup row — the §2
   "own org's tickets" canon is satisfied trivially since all portal-mockup rows are Bourla's) vs org-wide
   visibility via `Organization` sharing flags (osTicket parity). Decide before S6 user/org pages; if org-wide,
   widen QueueEngine portal scope + portal home/tickets together.
