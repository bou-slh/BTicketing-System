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
  both panels reflect it. Live-board leg still open until live.html ports.)*
- **S7 — Admin area** (42 pages, §6.3; B1–B4 everywhere, builders, settings actually consumed by the engine).
  *Gate*: every setting round-trips (flip block-work-until-approved → S4 guard flips); builder-created
  queue/form/list/filter demonstrably affects the agent panel.
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
- [ ] **live.html** — SignalR board: real column membership, drag between columns, Üstlen/claim, ticker from
      domain events, recoverable SLA countdown, pause (B7)
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
      bulkDone/bulkPartial.)*
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
      + lg.twofaHelpEmail. +9 tests (AgentProfileTests, incl. a full-pipeline render smoke) — suite 144.)*

### 6.3 Admin (42 pages)

Shared: every list page gets B1 (sort/search/pagination/selection/bulk/empty state), every dialog B2
(parameterized + correct submit), every settings page B3 (gating) + B9 scroll-spy, builders B4.

- [ ] **login.html** — real form, mandatory 2FA, forgot-password (B6)
- [ ] **pwreset.html** — admin reset-link flow, admin-only lockout policy (B6)
- [ ] **dashboard.html** — date range re-renders data-driven charts with tooltips; export; 3 stats tables sortable (B10)
- [ ] **system-info.html** — real server/PHP→.NET runtime/db info; update check
- [ ] **system-logs.html** — filters + Apply work; purge deletes; row detail dialog (B1/B2)
- [ ] **audit-logs.html** — filters/export/pagination; rows drill down to the audited object (B10)
- [ ] **settings-company.html** — logo file uploads with preview (B3); page selects live from SitePages
- [ ] **settings-system.html** — language add/remove rows (B4); attachment storage settings consumed; maintenance mode drives portal/offline
- [ ] **settings-tickets.html** — all 40 switches gate for real, effort section drives B8 guards; sequence dialog CRUD (B3/B4)
- [ ] **settings-tasks.html** — same pattern as tickets (B3/B4)
- [ ] **settings-agents.html** — template Edit dialogs per-row prefilled (B2); lockout policy consumed by B6
- [ ] **settings-users.html** — 6 template dialogs prefilled (B2); registration mode consumed by portal
- [ ] **settings-kb.html** — master switch gates portal KB visibility (B3)
- [ ] **email-settings.html** — 16 switches gate; footer button order normalized (S0)
- [ ] **emails.html / email-edit.html** — account CRUD; IMAP/SMTP config with per-protocol dialogs (B2), protocol
      radios gate fields (B3), OAuth2 tabs, **Test connection**
- [ ] **email-diagnostic.html** — real send with pending/success/failure states (B10)
- [ ] **templates.html / template-edit.html** — set CRUD (create dialog closes properly); 21 templates each
      open their own content (B2); variable pills click-to-insert (B5); per-template preview
- [ ] **banlist.html** — add/edit/delete real; B1
- [ ] **helptopics.html / helptopic-edit.html** — CRUD; number-format radio gates input; forms tab attach/detach (B3/B4)
- [ ] **filters.html / filter-edit.html** — CRUD; rule/action rows add/remove (B4); match-count preview; filters actually run in the mail/ticket pipeline
- [ ] **queues.html** — full builder: criteria/columns/sort/conditions rows (B4), drag-reorder columns, export
      column set, **live preview**, saved queue appears in agent queue tree
- [ ] **forms.html / form-edit.html** — form designer: field CRUD, per-field config dialog writes back (B2),
      type select reveals options editor, drag-reorder, live preview; output consumed by portal/agent open pages
- [ ] **lists.html / list-edit.html** — list editor: item CRUD + reorder (sort-mode gates), import dialog works,
      properties fields; system lists protected (B4)
- [ ] **slas.html** — per-row dialog prefilled (B2); grace/transient switches consumed by SLA engine
- [ ] **schedules.html / schedule-edit.html** — entry/holiday rows add/remove (B4); timezone; **diagnostic answers**; Clone works
- [ ] **pages.html** — site page CRUD (B2); pages served on portal
- [ ] **apikeys.html** — key CRUD + regenerate + copy-to-clipboard; IP restriction enforced by the API (B2)
- [ ] **plugins.html** — feature-flag install/enable/disable per module; per-module configure entry (§3 replaced)
- [ ] **staff.html / staff-edit.html** — CRUD; permission cards master↔children (B3); access/team rows (B4);
      LDAP/auth-backend select gates fields; password dialog validated (B2)
- [ ] **teams.html** — per-row dialog prefilled; member roster add/remove (B2/B4)
- [ ] **roles.html / role-edit.html** — 42-box matrix with tri-state masters (B3); role consumed by authz on every endpoint
- [ ] **departments.html / department-edit.html** — CRUD with hierarchy (collapse); autoresponder switches gate;
      access rows (B3/B4); export

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
