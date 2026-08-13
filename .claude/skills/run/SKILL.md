---
name: run
description: Start the app for development or manual testing — database up, migrations applied, dotnet watch, open the page under test.
---

1. `docker compose up -d db` and wait for healthy (`docker compose ps` shows healthy; the compose healthcheck is pg_isready).
2. Start the app in the background: `dotnet watch --project src/RapidsolDestek.Web --non-interactive` (Development env auto-migrates and seeds; note the listening URL, default http://localhost:5000).
3. Wait until the URL responds, then open the page under test (or `$ARGUMENTS` if a path was given) — via the playwright MCP browser when verification is the goal, or report the URL for the user.
4. Seeded dev credentials: staff `uakin` (admin, TOTP from `Seed:AdminTotpKey`), `mcetin`/`dkaya`/... (agents); customer `bourla.salehi@ulasim.com.tr`. Password: `Seed:Password` config, default `rapidsol1dev`.
