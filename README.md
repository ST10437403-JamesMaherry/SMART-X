# Smart-X IoT Mesh Ecosystem

**PROG7312 Portfolio of Evidence - 2026**

Smart-X is a simulated Internet of Things (IoT) telemetry management ecosystem developed for the PROG7312 Portfolio of Evidence.

Part 1 implements the **Smart-X Data Ingestion and Validation Gateway**. The backend can register sensors, validate and ingest strongly typed telemetry, process historical telemetry batches, validate recursive deployment structures, and securely store sensor-related configuration files, deployment photographs, and hardware logs.

The project uses a layered .NET 10 architecture with a React + TypeScript client so that later PoE stages can extend the same foundation with real-time command processing, anomaly management, device history, and IoT mesh-network routing.

---

## Student Information

- **Student:** James Maherry
- **Student Number:** ST10437403
- **Module:** Programming 3B
- **Module Code:** PROG7312

---

## Part 1 Status

### Backend

The Part 1 backend implementation is complete for the current sensor, telemetry, deployment, and attachment requirements.

Implemented functionality includes:

- Sensor registration
- Sensor listing and lookup
- Sensor profile updates
- MAC address and custom identifier validation
- Atomic duplicate device-identifier protection
- Structured deployment locations
- Sensor categories and operational statuses
- Strongly typed telemetry ingestion
- Float, integer, and boolean telemetry
- Historical jagged telemetry batch processing
- Recent telemetry retrieval
- Generic telemetry collections
- Numeric telemetry operator overloading
- Recursive deployment-tree validation
- Secure multipart attachment uploads
- Registered-sensor attachment enforcement
- File extension and media-type validation
- Image file-signature validation
- 10 MiB attachment size limit
- AES-256-GCM authenticated encryption
- Encrypted filesystem storage
- Secure streaming attachment downloads
- Centralised `ProblemDetails` API errors
- Cancellation-token propagation
- Thread-safe in-memory sensor and telemetry repositories
- Unit, infrastructure, API integration, and cross-feature workflow tests

### Frontend

The React + TypeScript client is scaffolded and included in CI.

The remaining Part 1 work is primarily frontend-focused:

- Build the Part 1 landing/dashboard experience
- Connect the React client to the sensor and telemetry APIs
- Add sensor registration and management UI
- Add telemetry simulation and history UI
- Add secure attachment upload/download UI
- Implement the researched **Adaptive Anomaly Spotlight** engagement strategy
- Keep the Part 2 and final PoE pillars visible but disabled
- Complete final UI regression testing and assessment evidence

---

## Architecture

```text
React + TypeScript Client
          |
          | HTTP / JSON / multipart
          v
     SmartX.Api
          |
          v
   SmartX.Application
      /         \
     v           v
SmartX.Domain  Repository Abstractions
                   |
                   v
           SmartX.Infrastructure
             /            \
            v              v
      In-Memory Data   Encrypted Files
```

The architecture separates transport, application orchestration, domain rules, and infrastructure concerns.

### `SmartX.Domain`

Contains the core business model and advanced C# concepts:

- `SensorProfile`
- `DeviceIdentifier`
- `DeploymentLocation`
- Sensor enums and validation rules
- `TelemetryPacket<T>`
- `NumericTelemetryPacket<T>`
- `TelemetryDelta<T>`
- `HistoricalTelemetryBuffer<T>`
- Recursive deployment-tree models and validation
- Domain-specific exceptions

### `SmartX.Contracts`

Contains transport-safe API request and response models.

Domain entities are not exposed directly through the HTTP API.

### `SmartX.Application`

Contains application workflows and repository abstractions:

- Sensor registration
- Sensor querying and updates
- Sensor contract mapping
- Strongly typed telemetry orchestration
- Historical telemetry batch processing
- Attachment validation
- Registered-sensor attachment enforcement
- Repository interfaces

### `SmartX.Infrastructure`

Contains concrete infrastructure implementations:

- Thread-safe in-memory sensor repository
- Strongly typed in-memory telemetry repository
- Chunked AES-256-GCM attachment encryption
- Encrypted filesystem attachment storage
- Attachment metadata persistence

### `SmartX.Api`

Contains:

- ASP.NET Core controllers
- Dependency injection
- CORS configuration
- OpenAPI support
- Centralised `ProblemDetails`
- Streaming multipart parsing
- Sensor endpoints
- Telemetry endpoints
- Attachment endpoints

### `SmartX.Client`

Contains the React + TypeScript frontend.

The client is currently being developed against the completed Part 1 backend API.

---

## Solution Structure

```text
Smart-X/
├── src/
│   ├── SmartX.Domain/
│   ├── SmartX.Contracts/
│   ├── SmartX.Application/
│   ├── SmartX.Infrastructure/
│   ├── SmartX.Api/
│   └── SmartX.Client/
│
├── tests/
│   ├── SmartX.Domain.Tests/
│   ├── SmartX.Application.Tests/
│   ├── SmartX.Infrastructure.Tests/
│   └── SmartX.Api.IntegrationTests/
│
├── docs/
├── .github/
│   └── workflows/
│       └── ci.yml
└── SmartX.sln
```

---

# Core Part 1 Features

## Sensor Management

Each Smart-X sensor profile contains:

- Internal sensor ID
- Human-readable name
- MAC address or custom unique identifier
- Deployment facility
- Deployment zone
- Deployment node
- Sensor category
- Operational status
- Expected telemetry data type
- Creation timestamp
- Update timestamp

Supported sensor categories are:

```text
Environmental
PowerConsumption
Actuator
```

Supported telemetry types are:

```text
Float
Integer
Boolean
```

New sensors begin in the `Offline` state.

### Device Identifiers

Smart-X supports:

```text
MacAddress
UniqueIdentifier
```

Equivalent MAC address formats are normalised to one canonical representation.

For example:

```text
aa-bb-cc-dd-ee-ff
AABBCCDDEEFF
AA:BB:CC:DD:EE:FF
```

become:

```text
AA:BB:CC:DD:EE:FF
```

The repository maintains an identifier index so duplicate device registrations can be rejected efficiently and safely.

Sensor identifiers are immutable after registration.

---

## Strongly Typed Telemetry

Smart-X uses generics so telemetry values remain in their native value types.

```csharp
TelemetryPacket<double>
TelemetryPacket<int>
TelemetryPacket<bool>
```

The application maps registered sensor data types as follows:

```text
Float    -> NumericTelemetryPacket<double>
Integer  -> NumericTelemetryPacket<int>
Boolean  -> TelemetryPacket<bool>
```

This avoids weakening telemetry payloads to `object` or `dynamic`.

A sensor configured for one telemetry type cannot accept a different payload type.

For example, a `Float` sensor cannot accept an `IntegerValue` payload.

---

## Numeric Telemetry and Operator Overloading

Numeric telemetry uses:

```csharp
NumericTelemetryPacket<T>
```

The subtraction operator compares two chronological readings from the same device.

```text
Previous reading: 22.4
Current reading:  27.1

Delta: +4.7
```

The result is represented by:

```csharp
TelemetryDelta<T>
```

and includes:

- Previous value
- Current value
- Signed delta
- Absolute magnitude
- Elapsed time
- Increase/decrease/unchanged state

This provides a meaningful domain use for operator overloading and can later support anomaly and rate-of-change analysis.

---

## Historical Telemetry Batch Processing

Raw historical readings are accepted as jagged batches because individual telemetry batches may contain different numbers of readings.

```text
Batch 0 -> [10, 11]
Batch 1 -> [20]
Batch 2 -> [30, 31, 32]
```

The application converts raw jagged arrays into:

```csharp
HistoricalTelemetryBuffer<T>
```

The custom collection is backed by optimised `List<T>` storage while preserving:

- Original batch order
- Packet sequence order
- Empty batches
- Uneven batch sizes
- Strong generic types
- Original batch boundaries

The historical buffer is used by the real telemetry ingestion workflow rather than existing only as an isolated data-structure demonstration.

---

## Recursive Deployment Validation

Smart-X supports hierarchical deployment trees such as:

```text
Facility
├── Zone A
│   ├── Node 1
│   └── Node 2
└── Zone B
    └── Node 3
```

Recursive validation traverses nested deployment structures while maintaining clear validation rules and base cases.

This gives recursion a direct Smart-X purpose: validating arbitrarily nested IoT deployment configurations.

---

# Secure Sensor Attachments

Smart-X supports three attachment categories:

```text
Configuration
DeploymentPhoto
HardwareLog
```

Attachments can only be associated with a sensor that is currently registered in Smart-X.

## Supported File Types

### Configuration

```text
.json
.yaml
.yml
.cfg
.txt
```

### Deployment Photos

```text
.jpg
.jpeg
.png
.webp
```

### Hardware Logs

```text
.log
.txt
.json
.csv
```

The application validates file extensions and allowed media types before storage.

Deployment photographs receive additional binary file-signature validation.

Supported binary signatures currently include:

- JPEG
- PNG
- WebP

This prevents a client from bypassing validation simply by renaming an arbitrary file and supplying a false image media type.

---

## Streaming Upload Pipeline

Smart-X does not load complete attachments into memory.

```text
HTTP Request Body
        |
        v
MultipartReader
        |
        v
Small Signature Prefix Inspection
        |
        v
Prefix Replay Stream
        |
        v
Size-Limited Stream
        |
        v
Chunked AES-256-GCM
        |
        v
Encrypted Filesystem Storage
```

Only a small leading prefix is read for binary signature inspection. Those bytes are replayed into the downstream stream so the stored attachment remains byte-for-byte equivalent to the original upload.

The maximum attachment size is:

```text
10 MiB
```

The limit is enforced while the stream is consumed rather than trusting only a client-provided `Content-Length` header.

---

## Attachment Security

Controls include:

- Registered sensor validation
- Attachment category validation
- File-name validation
- Control-character rejection
- Path-separator rejection
- Extension allowlists
- Media-type allowlists
- JPEG/PNG/WebP signature inspection
- Empty-file rejection
- Streaming size enforcement
- Generated physical storage names
- Storage outside the public web root
- AES-256-GCM authenticated encryption
- Ciphertext tamper detection
- Ciphertext truncation detection
- Cancellation propagation
- Partial-file cleanup after persistence failures
- `Cache-Control: no-store` on downloads
- `X-Content-Type-Options: nosniff` on downloads

Client file names are metadata only.

A user upload such as:

```text
gateway.log
```

is stored physically using generated identifiers instead of the client-controlled name.

Example:

```text
68b4a84e00a7421782048f672df94c63.payload
68b4a84e00a7421782048f672df94c63.metadata.json
```

---

## Attachment Encryption

Attachment payloads are encrypted before persistent storage using:

```text
AES-256-GCM
```

Smart-X uses chunked authenticated encryption so large files do not need to be buffered as one block.

Each chunk contains a unique nonce-derived encryption operation and authentication tag.

```text
Plaintext Stream
       |
       v
64 KiB Chunk
       |
       v
AES-256-GCM
       |
       +--> Ciphertext
       |
       +--> Authentication Tag
       |
       v
Encrypted Payload
```

A final authenticated record is written so truncated encrypted files can be detected.

Plaintext attachment files are not intentionally written to persistent storage.

---

# API Reference

## Health

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/health` | API health check |

## Sensors

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/api/sensors` | Register a sensor |
| `GET` | `/api/sensors` | List registered sensors |
| `GET` | `/api/sensors/{id}` | Retrieve one sensor |
| `PUT` | `/api/sensors/{id}` | Update mutable sensor details |

### Register Sensor Example

```json
{
  "name": "Greenhouse Temperature Sensor",
  "deviceIdentifier": "AA:BB:CC:DD:EE:01",
  "identifierKind": "MacAddress",
  "deploymentLocation": {
    "facility": "Cape Town Facility",
    "zone": "Greenhouse A",
    "nodeId": "Node-01"
  },
  "category": "Environmental",
  "telemetryDataType": "Float"
}
```

Successful registration returns `201 Created`.

Duplicate device identifiers return `409 Conflict`.

Invalid sensor data returns `400 Bad Request`.

Unknown sensors return `404 Not Found`.

---

## Telemetry

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/api/sensors/{id}/telemetry` | Ingest one reading |
| `POST` | `/api/sensors/{id}/telemetry/batches` | Ingest historical jagged batches |
| `GET` | `/api/sensors/{id}/telemetry?limit=50` | Return recent telemetry |

The recent telemetry limit defaults to `50` and supports values from `1` to `100`.

### Float Telemetry

```json
{
  "capturedAtUtc": "2026-09-11T10:30:00Z",
  "floatValue": 24.75,
  "integerValue": null,
  "booleanValue": null
}
```

### Integer Telemetry

```json
{
  "capturedAtUtc": "2026-09-11T10:30:00Z",
  "floatValue": null,
  "integerValue": 4200,
  "booleanValue": null
}
```

### Boolean Telemetry

```json
{
  "capturedAtUtc": "2026-09-11T10:30:00Z",
  "floatValue": null,
  "integerValue": null,
  "booleanValue": false
}
```

Exactly one value must match the registered sensor's configured telemetry type.

---

## Attachments

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/api/sensors/{id}/attachments?category={category}` | Upload an attachment |
| `GET` | `/api/sensors/{id}/attachments` | List attachment metadata |
| `GET` | `/api/sensors/{id}/attachments/{attachmentId}` | Download and decrypt an attachment |

Valid categories:

```text
Configuration
DeploymentPhoto
HardwareLog
```

Example:

```bash
curl -k \
  -F "file=@gateway.log;type=text/plain" \
  "https://localhost:<PORT>/api/sensors/<SENSOR-ID>/attachments?category=HardwareLog"
```

The `<SENSOR-ID>` must refer to an already registered sensor.

---

# API Error Responses

Smart-X uses centralised RFC-style `ProblemDetails` responses.

| Status | Meaning |
|---|---|
| `400 Bad Request` | Invalid request, domain validation, telemetry mismatch, or invalid attachment |
| `404 Not Found` | Requested sensor or attachment resource does not exist |
| `409 Conflict` | Duplicate sensor device identifier |
| `500 Internal Server Error` | Unexpected server-side failure |

Malformed JSON and model-binding errors also return validation `ProblemDetails`.

Unexpected internal exception details are not exposed to API clients.

---

# Adaptive Anomaly Spotlight

The Part 1 research task selected the **Adaptive Anomaly Spotlight** as the dashboard engagement strategy.

The planned frontend feature will prioritise sensors using factors such as:

- Severity
- Recency
- Disconnect duration
- Latest telemetry state

The intention is to move an operator from a fleet-level warning to the most relevant sensor, telemetry, configuration, and hardware-log evidence without forcing them to search manually through unrelated data.

The feature is part of the remaining Part 1 React implementation.

---

# Technology Stack

## Backend

- C#
- .NET 10
- ASP.NET Core Web API
- OpenAPI
- xUnit
- AES-256-GCM

## Frontend

- React
- TypeScript
- Vite
- ESLint
- Prettier

## Development and Delivery

- Visual Studio Code
- Git
- GitHub
- GitHub Actions

---

# Prerequisites

Install:

- .NET 10 SDK
- Node.js 24 or later
- npm
- Git

OpenSSL is recommended on macOS/Linux for generating a local attachment encryption key.

Check installed versions:

```bash
dotnet --version
node --version
npm --version
git --version
```

---

# Setup

## 1. Restore the Backend

From the repository root:

```bash
dotnet restore SmartX.sln
```

## 2. Configure Attachment Encryption

Attachment functionality requires a Base64-encoded 32-byte encryption key.

### macOS / Linux

```bash
export Attachments__EncryptionKeyBase64="$(openssl rand -base64 32)"
```

The variable applies to the current terminal session.

### Windows PowerShell

```powershell
$bytes = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
$env:Attachments__EncryptionKeyBase64 = [Convert]::ToBase64String($bytes)
```

Do not commit encryption keys to `appsettings.json`, `appsettings.Development.json`, `.env`, or GitHub.

## 3. Build the Backend

```bash
dotnet build SmartX.sln
```

Release build:

```bash
dotnet build SmartX.sln \
  --configuration Release \
  --no-restore
```

## 4. Run the API

```bash
dotnet run --project src/SmartX.Api
```

Use the HTTP/HTTPS address printed by ASP.NET Core in the terminal.

## 5. Install Frontend Dependencies

Open a second terminal:

```bash
cd src/SmartX.Client
npm ci
```

## 6. Run the React Client

From `src/SmartX.Client`:

```bash
npm run dev
```

Vite prints the local client address in the terminal.

The development CORS configuration allows the Vite development origin configured in `appsettings.Development.json`.

---

# Automated Testing

Smart-X uses four automated test projects:

```text
SmartX.Domain.Tests
SmartX.Application.Tests
SmartX.Infrastructure.Tests
SmartX.Api.IntegrationTests
```

The backend currently contains **more than 200 automated tests** covering Domain, Application, Infrastructure, HTTP integration, and complete cross-feature workflows.

Coverage includes:

- Sensor profile validation
- MAC and unique identifiers
- Duplicate-registration protection
- Sensor querying and updating
- Generic telemetry
- Numeric generic telemetry
- Operator overloading
- Invalid telemetry type rejection
- Historical jagged batches
- Batch ordering and boundaries
- Generic collection conversion
- Recursive deployment validation
- Thread-safe sensor persistence
- Thread-safe telemetry persistence
- Attachment validation
- File-signature validation
- Size limits
- Path traversal prevention
- AES encryption and decryption
- Tamper detection
- Truncation detection
- Encrypted filesystem persistence
- Cancellation and cleanup
- Sensor-specific attachment enforcement
- API `ProblemDetails`
- Malformed JSON handling
- Complete sensor + telemetry + attachment workflows
- Cross-sensor resource isolation

Run all backend tests:

```bash
dotnet test SmartX.sln
```

Run individual projects:

```bash
dotnet test tests/SmartX.Domain.Tests/SmartX.Domain.Tests.csproj

dotnet test tests/SmartX.Application.Tests/SmartX.Application.Tests.csproj

dotnet test tests/SmartX.Infrastructure.Tests/SmartX.Infrastructure.Tests.csproj

dotnet test tests/SmartX.Api.IntegrationTests/SmartX.Api.IntegrationTests.csproj
```

Run Release tests:

```bash
dotnet test SmartX.sln \
  --configuration Release \
  --no-build
```

---

# Code Coverage

The test projects use `coverlet.collector`.

Generate coverage locally:

```bash
rm -rf TestResults

dotnet test SmartX.sln \
  --configuration Release \
  --collect:"XPlat Code Coverage" \
  --results-directory TestResults
```

Find the generated reports:

```bash
find TestResults \
  -type f \
  -name "coverage.cobertura.xml" \
  -print
```

`TestResults` is generated test output and should not be committed.

---

# Backend Quality Checks

From the repository root:

```bash
dotnet restore SmartX.sln \
  -p:NuGetAudit=true \
  -p:NuGetAuditMode=all \
  -warnaserror:"NU1901;NU1902;NU1903;NU1904"

dotnet format SmartX.sln \
  --verify-no-changes \
  --no-restore

dotnet build SmartX.sln \
  --configuration Release \
  --no-restore

dotnet test SmartX.sln \
  --configuration Release \
  --no-build

git diff --check
```

---

# Frontend Quality Checks

From `src/SmartX.Client`:

```bash
npm ci
npm audit --audit-level=high
npm run lint
npm run format:check
npm run build
```

These checks validate:

- Reproducible dependency installation
- Dependency security
- ESLint rules
- Prettier formatting
- TypeScript compilation
- Production Vite build

---

# Continuous Integration

GitHub Actions validates changes targeting the integration and release branches.

The CI pipeline performs:

### Backend

- Repository checkout
- .NET 10 setup
- NuGet restore and vulnerability audit
- .NET formatting verification
- Release build
- Complete backend test suite

### Frontend

- Node.js 24 setup
- `npm ci`
- Dependency audit
- ESLint
- Prettier formatting check
- Production React/Vite build

Outdated runs are cancelled when a newer commit is pushed to the same branch.

---

# Storage and Runtime Notes

## Sensor and Telemetry Persistence

Part 1 currently uses process-local in-memory repositories for registered sensors and telemetry.

An API restart clears registered sensor and telemetry state.

The repository interfaces intentionally hide the storage technology so durable persistence can be introduced later without changing the API and Application workflows.

## Attachment Persistence

Encrypted attachments are written to:

```text
src/SmartX.Api/App_Data/attachments
```

unless `Attachments:StoragePath` is configured differently.

`App_Data` is excluded from Git.

Each sensor receives a generated storage directory containing:

```text
<attachment-id>.payload
<attachment-id>.metadata.json
```

Because sensor persistence is currently in-memory, stored attachments should be treated as belonging to the current Part 1 runtime session until durable sensor persistence is introduced.

---

# Git Workflow

Development uses short-lived feature branches and pull requests into `dev`.

Example:

```text
feature/11-13-complete-backend-api
```

Commit messages follow a conventional style such as:

```text
feat: add sensor repository and api endpoints
feat: add strongly typed telemetry application pipeline
feat: expose telemetry ingestion and retrieval api
feat: harden secure sensor attachment validation
test: complete backend integration coverage
docs: document completed backend api
```

The `dev` branch is used as the integration branch before final delivery.

---

# Remaining Part 1 Work

The core backend is ready for frontend integration.

Remaining work is focused on:

- React Part 1 dashboard
- Startup navigation with the three Smart-X architectural pillars
- Sensor registration and management UI
- Telemetry simulation and history UI
- Attachment upload/list/download UI
- Adaptive Anomaly Spotlight
- Responsive UI and clear feedback states
- Client-side validation
- Final frontend/backend integration testing
- Final assessment documentation and demonstration

The Part 1 startup interface will present:

```text
Sensor Data Ingestion and Telemetry
    -> Active

Real-Time Command Stream and History
    -> Disabled until Part 2

Network Topology and Mesh Routing
    -> Disabled until the final PoE
```

---

# Future Smart-X Functionality

Part 2 will extend Smart-X into real-time processing and device management, including:

- FIFO telemetry queues
- Priority queues for critical alerts
- Command history and undo
- Device-state lookups
- Timestamp-organised history
- Active error-state tracking
- Real-time dashboard behaviour

The final PoE will extend the same architecture with:

- Historical data trees
- Network graphs
- Graph traversal
- IoT mesh topology
- Minimum spanning trees
- Routing visualisation
- Complete application integration

---

# Security Notes

- Never commit the attachment encryption key.
- Never use uploaded file names as physical storage paths.
- Do not expose encrypted storage through `wwwroot`.
- Treat request `Content-Type`, file extensions, and user-supplied metadata as untrusted input.
- Use HTTPS outside local development.
- Use a securely managed persistent secret when Smart-X is deployed beyond local development.
- Replace the Part 1 in-memory repositories with durable persistence before treating Smart-X as a production system.

---

# Current Development Milestone

The Smart-X Part 1 backend now provides a complete foundation for:

```text
Sensor Registration
        |
        +--> Sensor Management
        |
        +--> Strongly Typed Telemetry
        |       |
        |       +--> Individual Readings
        |       +--> Historical Batches
        |       +--> Recent History
        |
        +--> Recursive Deployment Validation
        |
        +--> Secure Sensor Attachments
                |
                +--> Streaming Multipart Uploads
                +--> Signature Validation
                +--> AES-256-GCM Encryption
                +--> Secure Download
```

The next major development stage is the React Part 1 dashboard and Adaptive Anomaly Spotlight integration.
