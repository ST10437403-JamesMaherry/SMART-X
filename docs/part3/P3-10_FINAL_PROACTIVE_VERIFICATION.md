# P3-10 — Final proactive network and authentic-learning verification

## Scope and independence

**Flow A (new):** `e2e/part3-proactive-network-insights.spec.ts` uses actual HTTP registration, simulated mesh links and operational status mutations to prove that a first-visit graph disconnection surfaces an explained, rule-derived warning in **Topology and Operations**, without a user search or a relay command. It checks real affected IDs/names, evidence source, server graph revision, recovery and stable cache timestamps, no repeated alerts, no unintended search event recording, 375px layout and recoverable API failures.

**Flow B (pre-existing, deliberately preserved):** `e2e/part2-suggested-actions-journey.spec.ts` exercises the original Part 2 genuine multi-episode `AlertViewed` → selected `SearchSubmitted` → actual accepted command learning, a fourth issue that produces an eligible suggestion, explicit review/confirmation, and auditable Undo. **Do not replace that flow with mocked training**: its real-API test remains the independent provenance proof.

The two kinds of guidance are **not interchangeable**. Topology rules have no learned confidence, no command target and no training effect. Suggested Actions use genuine supporting episodes, real current conditions, a heuristic score (not a calibrated probability), and an explicit server-revalidated command POST.

## Reproduction on a Mac (.NET 10 / Node 24)

Start from the repository root on `feature/part3-proactive-insights`. The E2E runner starts an isolated Development-mode API at `http://127.0.0.1:5095` and Vite at `http://localhost:5173`. Do not aim simulation tests at production.

```bash
cd src/SmartX.Client
npm ci
npm run lint
npm run format:check
npm run build
CI=true npm run test:e2e -- --grep 'P3-10 proactive network insights and evidence separation' --retries=0
CI=true npm run test:e2e -- --grep 'P2-10 genuine learn / suggest / execute / Undo journey' --retries=0
CI=true npm run test:e2e -- --retries=0
cd ../..
dotnet restore SmartX.sln
dotnet build SmartX.sln -c Release --no-restore
dotnet format SmartX.sln --verify-no-changes --no-restore
dotnet test SmartX.sln -c Release --no-build
```

## Manual witness scenario (real API)

1. Register two **Online** sensors; create gateway–relay and relay–leaf links using the simulation controls. Open Topology and Operations before searching for either sensor. Observe the graph and the explicit current-insight empty state (or other, unrelated existing current warnings if the process contains prior fixtures).
2. Set the relay **Offline**. The leaf stays registered and Online but loses gateway reachability. `GET /api/topology/insights` returns `GatewayDisconnected` with leaf UUID plus `NonForwardingDevices` with relay UUID. The UI labels the condition **Rule-derived · Not learned** and displays evidence, server evaluation time, and the real registered device's name.
3. Read the insights endpoint again without changing the graph. Confirm unchanged `graphVersion`, unchanged `projectionComputedAtUtc`, and stable `firstObservedAtUtc`/key. This demonstrates deduplication. A measurement of duration must be recorded independently, not invented from the repeated GET.
4. Restore relay Online. The current warnings disappear; a recovery notice is **optional** when the server's 60-second cooldown is active, otherwise it carries `isCurrent:false` and an explicit `expiresAtUtc`. A previously disconnected sensor becoming merely Offline is **not** a restoration.
5. In Operations, submit an actual device search. The bounded search-evidence panel shows the retained event and frequency count. Passive refreshes do not create more search events. The existing Suggested Actions section remains separate, with explicit review and no automatic actuator action.
6. Run the original genuine P2-10 learned-action journey separately. Verify its support from three different episodes, fourth alert without new search, and Undo remains an audited real command.

## Recorded verification (complete only after running locally)

| Gate | Expected | Actual result |
| --- | --- | --- |
| ESLint and Prettier | clean | Pending local run |
| Vite TypeScript production build | green | Pending local run |
| P3-10 new Playwright | 4 cases | Pending local run |
| Original P2-10 genuine learning | all cases pass | Pending local run |
| Complete Playwright | all prior cases pass | Pending local run |
| .NET format/build/test | clean | Pending local run |
| GitHub Actions PR | green | Pending review |

Do not state performance figures without collecting `Performance` traces or a measured summary in P3-11. Keep screenshots of both flows and compare revision/projection timestamps from real HTTP responses, never screenshot synthetic insight fixtures as the end-to-end result.

## Feedback ledger caveat

The user reported **18/30** for Part 2 Predictive Action and Suggestion Engine, with no detailed lecturer note. P3-10 adds demonstrable proactive awareness, honest provenance, authentic search-history visibility, and regression protection; this is a reasoned improvement, **not proof of the original deduction's exact cause** and not a retrospective score change.
