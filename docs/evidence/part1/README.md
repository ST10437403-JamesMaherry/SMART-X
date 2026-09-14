# Smart-X Part 1 Assessment Evidence

This directory contains selected visual evidence for the PROG7312 Smart-X Part 1 submission.

The screenshots are intentionally limited to behaviour that is useful for assessment verification. Implementation details that are better demonstrated through source code and automated tests are linked from the root `README.md`.

## Evidence

| File | Evidence |
|---|---|
| `01-gateway-navigation.png` | Three architectural pillars with Sensor Data Ingestion active and future modules disabled. |
| `02-sensor-registration.png` | Successful sensor registration including identifier, deployment location, category and telemetry type. |
| `03-typed-telemetry.png` | Telemetry simulator and recent telemetry history for a registered sensor. |
| `04-historical-batch-processing.png` | Historical batch simulator demonstrating uneven sequential batches used by the jagged-array ingestion workflow. |
| `05-adaptive-anomaly-spotlight.png` | Dynamic Adaptive Anomaly Spotlight displaying prioritised operational issues such as telemetry spikes or sensor disconnects. |
| `06-secure-attachments.png` | Sensor-linked multipart attachment workflow with upload metadata and secure download support. |
| `07-encrypted-storage.png` | Runtime evidence that attachment payloads are persisted using generated encrypted storage files rather than plaintext client filenames. |
| `08-responsive-layout.png` | Responsive Part 1 interface at mobile viewport dimensions. |
| `09-docker-deployment.png` | Healthy production API and React/Nginx containers under Docker Compose. |
| `10-ci-validation.png` | Successful Backend, Frontend, Playwright E2E and Docker Deployment GitHub Actions jobs. |

## Security

Screenshots in this directory must never expose:

- attachment encryption keys;
- `.env` contents;
- local secrets;
- authentication tokens;
- private credentials.

The root README provides the implementation and automated-test references corresponding to each visual artefact.
