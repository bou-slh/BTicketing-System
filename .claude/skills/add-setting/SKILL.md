---
name: add-setting
description: Add one admin setting end-to-end — typed setting, persistence, admin UI row, and the engine consuming it. Usage: /add-setting Tickets:EffortApproval:BlockWorkUntilApproved
---

Add the setting keyed in `$ARGUMENTS` end-to-end. A setting is only DONE when the engine actually consumes it (ROADMAP S7 gate: flip it → behavior flips).

1. Add the property to the matching typed settings section class (Infrastructure `Settings/`), with default value and validation.
2. Persistence: settings store row (key = the argument, dot/colon namespaced), included in seed defaults.
3. Admin UI: the correct `admin/settings-*.html` mockup section is the spec — add the `.rd-field` row (label + control + `.rd-help`) to the corresponding Razor view with per-view resx keys in TR+EN. Switches must gate their dependent fields (B3).
4. Consumer: wire the setting into the service/middleware that honors it; add both tests (enabled and disabled behavior) via **test-writer**.
5. `node tools/check-i18n.mjs`, `/verify`, commit as one slice: `S7: setting <key>`.
