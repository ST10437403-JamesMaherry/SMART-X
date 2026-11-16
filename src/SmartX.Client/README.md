# Smart-X Client

React + TypeScript frontend for the Smart-X IoT operations platform.

The client is not a standalone mock. Its three pillars call the real ASP.NET Core API and use SignalR only as an invalidation channel before re-reading authoritative HTTP state.

## Routes

| Route        | Pillar                               |
| ------------ | ------------------------------------ |
| `/`          | Smart-X gateway                      |
| `/ingestion` | Sensor Data Ingestion and Telemetry  |
| `/realtime`  | Real-Time Command Stream and History |
| `/topology`  | Network Topology and Mesh Routing    |

Route constants live in `src/routing/routes.ts`. Cross-pillar selected-device and safe draft state is retained by the shared workspace state provider.

## Requirements

The last recorded baseline was tested with Node.js `v24.18.0` and npm `11.16.0`.

Install the locked dependencies from this directory:

```bash
npm ci
```

## Development

Start the ASP.NET Core API first from the repository root:

```bash
dotnet dev-certs https --trust
export Attachments__EncryptionKeyBase64="$(openssl rand -base64 32)"
dotnet run --project src/SmartX.Api --launch-profile https
```

The Vite client defaults to `https://localhost:7095`, matching `src/SmartX.Client/.env.example`. The API profile also exposes `http://localhost:5203` for direct local scripts.

Then, from `src/SmartX.Client`:

```bash
npm run dev
```

Open `http://localhost:5173`.

## Quality gates

```bash
npm ci
npm audit --audit-level=high
npm run lint
npm run format:check
npm run build
npm run test:e2e
```

Available npm scripts are defined in `package.json`:

- `npm run dev` — Vite development server.
- `npm run build` — TypeScript project build followed by Vite production build.
- `npm run lint` — ESLint.
- `npm run format` — write Prettier formatting.
- `npm run format:check` — verify Prettier formatting.
- `npm run test:e2e` — Playwright Chromium regression.

Playwright starts isolated .NET and Vite servers according to `playwright.config.ts`. The recorded baseline included 99 passing browser tests, one intentional opt-in load test skip, and no failures.

## Production boundary

In Docker, the built static client is served by the unprivileged Nginx image. Nginx is the single browser-facing service and proxies `/api/` and `/hubs/` to the private API container.

See the repository root [`README.md`](../../README.md) for Docker setup, API examples, the system architecture, verification and security boundaries.
