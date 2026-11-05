# OrbStack Docker features for Dockyard: plan

**Hoofdvraag:** Which Docker-related OrbStack features must Dockyard have?

**Out of scope, by the owner's brief:** Kubernetes and VM features (Linux machines, isolated machines, USB, sound, VM resource limits).

**Run mode:** unattended, so the owner's brief stands in for approval at the gate. The owner asked for inline work without fan-out, so one worker answered all questions in sequence.

| # | Deelvraag | Bar | Method: what counts as an answer | Status |
|---|---|---|---|---|
| 1 | Which Docker features does OrbStack have today, and which are free? | `market` | Read every Docker page in the `docs.orbstack.dev` sitemap, the release notes for the last 12 months and the pricing page. Observe the main behaviours on the installed product. Answer: an inventory, each item graded observed, documented or claimed. | not-started |
| 2 | What does Dockyard do today for each of those features? | `technical` | Read README, `docs/backlog.md` and specs, then search `src`, `image`, `macos` and `scripts` at the current commit. Answer: per feature parity, partial, absent or on the backlog, with the file that shows it and the search behind any absence. | not-started |
| 3 | Which gaps break an ordinary workflow, and which do people value? | `market` + `technical` | Check Docker's own docs for the Docker Desktop baseline, MDN for browser rules, OrbStack's public tracker sorted by reactions, and third-party comparisons. Answer: per gap, whether the workflow fails or has a workaround, and what demand exists. | not-started |
