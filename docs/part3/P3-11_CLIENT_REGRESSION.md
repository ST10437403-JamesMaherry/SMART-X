# P3-11 — Integrated Three-Pillar Client Regression

## Purpose

This verification closes the browser-facing portion of P3-11 without changing
production behaviour. The final PoE requires the completed Smart-X application
to expose all three architectural pillars as one coherent interface. The
regression therefore exercises the shipped React client against the real
ASP.NET Core API rather than mocking route or sensor state.

## Covered behaviour

`src/SmartX.Client/e2e/part3-p3-11-integrated-regression.spec.ts` verifies:

1. **Three active pillars** — ingestion, real-time operations and topology are
   all accessible through the same semantic navigation.
2. **Asynchronous route changes** — moving between pillars does not reload the
   browser document, so the application-level state provider remains mounted.
3. **Safe state retention** — the selected registered sensor and safe text /
   numeric drafts remain available after visiting the other pillars.
4. **Server authority** — the selected sensor is registered and re-read through
   the real HTTP API; client state does not replace authoritative records.
5. **Topology resilience** — an injected browser-side topology GET failure
   produces an actionable error and recovers through the normal retry control
   without navigating away or clearing the selected device.
6. **Keyboard accessibility** — each architectural module link can be focused
   and activated using the keyboard, with `aria-current="page"` tracking the
   active route.
7. **Narrow responsive behaviour** — each completed pillar is checked at a
   320-pixel viewport and the document must not develop horizontal overflow.

## Why this belongs in P3-11

Earlier Part 3 issues already contain feature-level tests. P3-11 is the final
regression layer: it proves that those features continue to work together after
AVL history, graph traversal, Prim MST routing and the topology dashboard have
all been integrated. It does not duplicate algorithmic correctness evidence;
Commits 1 and 2 provide the large telemetry and mesh proofs for that purpose.

## Validation

Run the focused browser regression first:

```bash
cd src/SmartX.Client
npx playwright test e2e/part3-p3-11-integrated-regression.spec.ts
```

Then run the complete frontend quality gate:

```bash
npm audit --audit-level=high
npm run lint
npm run format:check
npm run build
npm run test:e2e
```

Return to the repository root and retain the normal backend regression:

```bash
cd ../..
dotnet format SmartX.sln --verify-no-changes --no-restore
dotnet build SmartX.sln -c Release --no-restore
dotnet test SmartX.sln -c Release --no-restore
git diff --check
```

Record the **actual** passing counts from the run. This document deliberately
does not hard-code a future test count because Commit 3 itself adds browser
coverage.
