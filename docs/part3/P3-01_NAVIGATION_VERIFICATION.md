# P3-01 - Three-Pillar Navigation Verification

**Issue:** `[P3-01] Establish Part 3 baseline and activate topology navigation`  
**Implementation branch:** `feature/part3-navigation`  
**Automated evidence:** `src/SmartX.Client/e2e/part3-navigation.spec.ts`

## Purpose and scope

P3-01 activates the third Smart-X navigation route while protecting the two
existing operational pillars. The page intentionally reports that mesh data is
**not available yet**: the adjacency graph, traversal and minimum-spanning-tree
APIs belong to later Part 3 issues. Gateway health does **not** imply topology
readiness, and this phase must not show fabricated nodes, connections or paths.

The route uses the existing React Router navigation and preserves the
`WorkspaceStateProvider` above the page components. This is an architectural
integration check, not evidence that mesh algorithms are already implemented.

## Automated regression matrix

| Case | Browser verification | Assessment relevance |
| --- | --- | --- |
| Direct routes and refresh | `/`, `/ingestion`, `/realtime`, `/topology`; correct active link; unknown URL fallback; hard reload | Three-pillar accessibility and routing |
| Keyboard and SPA navigation | Focus and Enter on all three native links; no document reload | Accessible asynchronous navigation |
| Browser history | Back/Forward across all pillars without losing a registration draft | Safe workspace-state retention |
| Real registered sensor | Selection and typed telemetry drafts survive topology; unsubmitted attachment is cleared; server record unchanged | Preserve genuine Part 1 and Part 2 workflows |
| Real-time search draft | Draft criteria survive topology; navigation creates no operator interaction | Preserve Part 2 learning integrity |
| Health and pending data | A reachable API is not misrepresented as an implemented mesh graph | Honest presentation of planned backend functionality |
| API failure and retry | Failed health GET exposes retry; recovery works without reload | Graceful degraded operation |
| Responsive layout and evidence | 1440, 768, 390 and 320 pixel widths; no document-level overflow; desktop/mobile screenshot attachments | Usability and demonstrable integration |

The existing Part 1 and Part 2 Playwright tests **must also pass**. All tests use
the actual HTTP API started by `playwright.config.ts`; only the deliberate
health-failure browser test intercepts an HTTP request.

## How to run

From the repository root:

```bash
cd src/SmartX.Client
npm run lint
npm run format:check
npm run build
npx playwright test e2e/part3-navigation.spec.ts
npm run test:e2e
cd ../..
git diff --check
```

The test runner starts the local API and Vite server automatically. Ensure ports
5095 and 5173 are available. Its standard configuration keeps one worker to
avoid racing the process-local sensor and interaction stores.

Run `npx playwright show-report` from `src/SmartX.Client` to inspect the HTML
report. The responsive test attaches **real browser screenshots** named
`part3-topology-1440px` and `part3-topology-320px`. Save any screenshots needed
for final assessment evidence under `docs/evidence/part3/` only after visually
checking that they represent a genuine passing run.

## Verification and traceability

Record the **actual** ESLint, Prettier, TypeScript/Vite, focused Playwright
and full-suite results in the P3-01 pull request after running these commands.
GitHub Actions results belong in the PR's checks; do not assume success based
on a historical snapshot or an unexecuted local command. The original Part 3
baseline check and its recorded results remain in `docs/part3/BASELINE.md`.

**Expected behaviour:** Three interactive pillars, preserved safe workspace
state, clear temporary mesh-data message and a recoverable API outage. **Not
claimed:** Implemented live topology graph, BFS, Prim MST, AVL range queries or
production deployment verification.

### Future-test maintenance

When P3-07/P3-08 introduce real topology responses, replace only the temporary
"Mesh data is not available yet" assertions with equivalent assertions against
a real backend response. Preserve the navigation, history, keyboard, retry,
responsive and cross-pillar regression coverage. Do not mock a fabricated mesh
to keep obsolete placeholders passing.
