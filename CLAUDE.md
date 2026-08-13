# RapidsolDestek

Turkish/English helpdesk product (portal + agent + admin panels) built from scratch in C#/ASP.NET Core.
osTicket (separate checkout) is a **domain-model reference only** — never copy its code (GPLv2).

## Golden rule: the mockups are the spec

`mockups/` contains 72 static TR/EN pages (portal 12, agent 18, admin 42). The shipped product must look
and behave exactly like them — **every visible component must work**, including controls that are dead in
the static HTML. The roadmap, parity checklists, and canonical sample data live in **`mockups/ROADMAP.md`**;
markup/i18n conventions live in `mockups/CONVENTIONS.md`. A page is done when its ROADMAP checklist is green
in TR+EN, light+dark — never when it merely renders.

## Stack

- .NET 10 · ASP.NET Core MVC, Razor areas `Portal` / `Agent` / `Admin`
- EF Core + PostgreSQL (migrations only via `dotnet ef` — never hand-edit `Migrations/`)
- Hangfire (jobs: mail fetch, SLA sweeps, retention) · SignalR (live board) · MailKit/MimeKit (email)
- xUnit (`tests/RapidsolDestek.Tests`) · Playwright E2E (`tests/RapidsolDestek.E2E`)

## Layout

```
src/RapidsolDestek.Domain          entities, domain services, no framework deps
src/RapidsolDestek.Infrastructure  EF Core, file store, email, external services
src/RapidsolDestek.Web             MVC app; Areas/{Portal,Agent,Admin}
tests/                             unit + E2E
mockups/                           the binding spec (do not edit without S0-style sign-off)
```

## Commands

- Run: `docker compose up -d db` then `dotnet watch --project src/RapidsolDestek.Web`
- Build: `dotnet build` · Tests: `dotnet test`
- Full stack: `docker compose up --build`

## Conventions

- Two auth principals: Customer (portal) and Staff (agent/admin), separate cookies. 2FA mandatory for admins.
- i18n: every UI string via `IStringLocalizer` + resx (tr default, en); no hardcoded literals in views.
  Key names mirror the mockup `data-i18n` keys.
- Every mutation writes an AuditEvent (EF interceptor). Status enum and seed data must match the canon
  table in `mockups/ROADMAP.md` §2.
- Effort approval (Efor Onayı) is the flagship flow — state machine `none→pending→approved|rejected→(revise→pending)`.
