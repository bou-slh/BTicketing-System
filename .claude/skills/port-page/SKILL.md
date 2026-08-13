---
name: port-page
description: Port one mockup page into the app — page-porter agent, then i18n audit and visual diff. The unit of work for stages S5–S7. Usage: /port-page mockups/agent/tickets.html
---

Port the mockup page given in `$ARGUMENTS` end-to-end:

1. Launch the **page-porter** agent for that page (one page per invocation). If porting several pages, launch multiple page-porter agents in parallel, each in an isolated worktree, never two agents on one page.
2. When the port completes, launch the **i18n-auditor** agent; fix any ERRORS it reports before continuing.
3. Launch the **visual-differ** agent with the app URL and mockup twin; fix defects and re-run until PASS (intentional deviations listed in the agent definition are acceptable).
4. Update the page's row in `mockups/ROADMAP.md` §6: tick items now working; leave TODO items unticked with a stage note.
5. Run `/verify`. Commit as one slice: `S<stage>: port <area>/<page>`.
