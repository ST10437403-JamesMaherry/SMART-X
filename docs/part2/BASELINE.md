# Smart-X Part 2 Baseline

Verified starting point for P2-01 before changing Part 1 behaviour.

- Verified at UTC: 2026-10-12T09:49:03.994Z
- Working branch: feature/part2-navigation
- Baseline commit: b66f16a05da8216d2b84fb3aaaa536c62ceadb41
- Part 1 release commit: fae212008dc5f00df8630b1bc41e1ecd2fb8f527
- Release tag: v1.0.0-part1
- Application source/configuration matches Part 1; only the committed frontend dependency lockfile security refresh differs.

## Tool versions

| Tool | Version |
|---|---|
| .NET SDK | 10.0.301 |
| Node.js | v24.18.0 |
| npm | 11.16.0 |
| Docker | Docker version 29.8.0, build 88096ef |
| Docker Compose | Docker Compose version v5.5.1 |

## Fresh test results

| Suite | Passed | Failed | Skipped | Flaky |
|---|---|---|---|---|
| Backend (4 projects) | 282 | 0 | 0 | n/a |
| Playwright | 3 | 0 | 0 | 0 |

Counts come from fresh TRX and Playwright JSON results from this run.

## Validation

| Check | Result |
|---|---|
| NuGet restore and vulnerability audit | Passed |
| .NET formatting | Passed |
| Backend Release build | Passed |
| Complete backend regression | Passed |
| Reproducible frontend dependency installation | Passed |
| Frontend high-severity dependency audit | Passed |
| Frontend lint | Passed |
| Frontend formatting | Passed |
| Frontend production build | Passed |
| Playwright Chromium availability | Passed |
| Real API and browser regression | Passed |
| Docker Compose configuration | Passed |
| Production container builds | Passed |
| Docker Compose startup | Passed |
| API and client health checks | Passed |
| Production React response | Passed |
| Part 1 direct-route response | Passed |
| Nginx API reverse proxy | Passed |
| Dashboard SignalR reverse proxy | Passed |
| Non-root API and frontend containers | Passed |
| API has no published host ports | Passed |
| SignalR transport negotiation | Passed |
| Production React shell and direct ingestion route | Passed |

These are local equivalents of the existing CI gates. They do not claim a new GitHub Actions run.

## Preserved baseline

- Part 1 release tag, source, tests, and evidence are retained.
- Existing attachment encryption configuration and named volume are retained.
- Sensors and telemetry remain process-local, as documented in Part 1.
- Root environment values, encryption keys, and raw hub tokens are excluded from this report.
- Part 2 navigation and backend features have not yet been changed.
- The next commit introduces the active Part 2 route and retained workspace state.
