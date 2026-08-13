---
name: verify
description: The "no detail neglected" gate — build, full test suite, i18n audit, Playwright smoke. Run at the end of every work unit and before every commit/PR.
---

Run the full verification gate; every step must pass:

1. `dotnet build RapidsolDestek.sln -v q` — zero warnings-as-errors regressions.
2. `dotnet test` — full suite (Testcontainers needs Docker; `docker compose up -d db` is NOT enough, tests bring their own container).
3. `node tools/check-i18n.mjs` — mockups + resx ALL CLEAN.
4. Playwright smoke via the playwright MCP (app running: `docker compose up -d db` + `dotnet run --project src/RapidsolDestek.Web`): load `/login`, `/agent/login`, `/admin/login`; sign in as the seeded customer and staff users; confirm chrome renders (header/topbar/sidebar) and the language select flips TR⇄EN.
5. If any page was ported/changed this unit, the **visual-differ** agent must have passed on it.

Report each step's result; on failure, stop and fix before proceeding — never commit on a red gate.
