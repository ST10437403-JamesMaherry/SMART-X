# Smart-X — Early UI and Deployment Screenshots

This directory preserves historical screenshots from an early Smart-X release. Screenshots are not substitutes for current automated test results.

They capture the UI and deployment state at the time they were taken; current routes, features, and checks should be verified against the source code and CI configuration.

## Evidence

| File | Evidence |
|---|---|
| `01-gateway-navigation.png` | Early gateway navigation before all architectural pillars were enabled. |
| `02-sensor-registration.png` | Successful sensor registration including identifier, deployment location, category and telemetry type. |
| `03-typed-telemetry.png` | Telemetry simulator and recent telemetry history for a registered sensor. |
| `04-historical-batch-processing.png` | Historical batch simulator demonstrating uneven sequential batches used by the jagged-array ingestion workflow. |
| `05-adaptive-anomaly-spotlight.png` | Dynamic Adaptive Anomaly Spotlight displaying prioritised operational issues such as telemetry spikes or sensor disconnects. |
| `06-secure-attachments.png` | Sensor-linked multipart attachment workflow with upload metadata and secure download support. |
| `07-encrypted-storage.png` | Runtime evidence that attachment payloads are persisted using generated encrypted storage files rather than plaintext client filenames. |
| `08-responsive-layout.png` | Responsive early interface at mobile viewport dimensions. |
| `09-docker-deployment.png` | Healthy production API and React/Nginx containers under Docker Compose. |
| `10-ci-validation.png` | Successful Backend, Frontend, Playwright E2E and Docker Deployment GitHub Actions jobs. |

## Security

Screenshots in this directory must never expose:

- attachment encryption keys;
- `.env` contents;
- local secrets;
- authentication tokens;
- private credentials.

See the root README for the current architecture, setup and security limitations.
