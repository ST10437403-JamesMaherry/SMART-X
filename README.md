# Smart-X IoT Mesh Ecosystem

**PROG7312 Portfolio of Evidence - 2026**

Smart-X is a simulated Internet of Things (IoT) telemetry management system developed for the PROG7312 Portfolio of Evidence.

The system is designed to register IoT sensors, process strongly typed telemetry readings, manage historical telemetry batches, validate deployment hierarchies, securely store sensor attachments, detect important changes, and eventually manage a simulated IoT mesh network.

---

## Student Information

- **Student:** James Maherry
- **Student Number:** ST10437403
- **Module:** Programming 3B
- **Module Code:** PROG7312

---

## What Smart-X Does

Smart-X acts as a central gateway for simulated IoT devices.

Sensors may report information such as:

- Temperature readings
- Power consumption
- Moisture levels
- Valve or actuator states

Smart-X identifies which device produced a reading, validates the data, processes telemetry using strongly typed models, manages historical readings, validates sensor deployment structures, and securely manages sensor-related files.

The completed Smart-X ecosystem will eventually include sensor management, telemetry processing, anomaly detection, device history, command processing, encrypted attachments, and IoT mesh-network routing.

---

# Part 1

Part 1 focuses on building the **Smart-X Data Ingestion and Validation Gateway**.

The Part 1 implementation currently demonstrates advanced C# concepts including:

- Object-oriented domain modelling
- Generic programming
- Generic mathematics
- Operator overloading
- Jagged arrays
- Generic collections
- Recursion
- Stream-based processing
- Authenticated encryption
- Automated testing

The application uses a layered architecture consisting of:

```text
React Client
     |
     v
ASP.NET Core API
     |
     v
Application Layer
     |
     v
Domain / Infrastructure
```

---

# Current Progress

The project currently includes:

- Clean multi-project .NET solution architecture
- ASP.NET Core Web API foundation
- React and TypeScript frontend
- Automated GitHub Actions CI
- Sensor domain model
- Sensor profile validation
- MAC address and custom device identifiers
- Structured deployment locations
- Sensor categories and operational statuses
- Telemetry data type definitions
- Domain-specific validation exceptions
- UTC creation and update timestamps
- Generic telemetry packets
- Numeric generic telemetry
- Meaningful operator overloading
- Historical telemetry batch processing
- Jagged array processing
- Array-to-collection conversion
- Recursive deployment hierarchy validation
- Secure sensor media and log attachments
- Streaming multipart uploads
- AES-256-GCM authenticated encryption
- Encrypted filesystem storage
- Comprehensive automated testing

---

# Sensor Domain Model

Each registered Smart-X sensor has a domain profile containing information such as:

- Sensor name
- Device MAC address or unique identifier
- Deployment location
- Sensor category
- Sensor status
- Expected telemetry data type
- Creation timestamp
- Update timestamp

The domain model validates this information before it can be used by higher application layers.

Device identifiers support both standard MAC addresses and custom Smart-X identifiers.

MAC addresses are normalised into a consistent format so that equivalent device identifiers can be compared reliably.

Deployment locations use a structured hierarchy such as:

```text
Facility
└── Zone
    └── Node
```

---

# Generic Telemetry

Smart-X uses generics to keep incoming sensor readings strongly typed.

The main telemetry model is:

```csharp
TelemetryPacket<T>
```

Examples include:

```csharp
TelemetryPacket<double>
TelemetryPacket<int>
TelemetryPacket<bool>
```

This allows different sensor types to share a common telemetry structure without storing telemetry values as `object`.

For example:

```text
Temperature sensor  -> double
Power counter       -> int
Valve state         -> bool
```

Each telemetry packet contains information including:

- Source device identifier
- Strongly typed payload
- UTC capture timestamp

Using generic models avoids unnecessary boxing, casting, and runtime type conversion.

---

# Numeric Telemetry and Operator Overloading

Numeric sensor readings use:

```csharp
NumericTelemetryPacket<T>
```

This supports numeric telemetry such as:

```csharp
NumericTelemetryPacket<double>
NumericTelemetryPacket<int>
```

Smart-X overloads the subtraction operator so that two compatible numeric sensor readings can be compared directly.

For example:

```text
Previous temperature: 22.4°C
Current temperature:  27.1°C

Current - Previous = +4.7°C
```

The result is represented by:

```csharp
TelemetryDelta<T>
```

A telemetry delta contains information such as:

- Previous value
- Current value
- Signed delta
- Absolute magnitude
- Elapsed time
- Increase state
- Decrease state
- Unchanged state

The overloaded operator has a meaningful purpose within the Smart-X domain because telemetry differences can later support:

- Spike detection
- Sudden drop detection
- Anomaly detection
- Rate-of-change analysis
- Threshold monitoring

Boolean telemetry continues to use:

```csharp
TelemetryPacket<bool>
```

Boolean telemetry is intentionally excluded from numeric subtraction because subtracting boolean states has no meaningful domain purpose.

---

# Historical Telemetry Batch Processing

Smart-X supports historical telemetry batch processing using both arrays and optimised generic collections.

Raw sequential telemetry batches are represented using jagged arrays.

This demonstrates the use of advanced array structures while supporting batches that may contain different numbers of telemetry readings.

The historical processing pipeline converts raw array-based input into:

```csharp
List<T>
```

collections for easier downstream processing.

The implementation preserves:

- Batch ordering
- Reading ordering
- Generic telemetry types
- Sequential processing behaviour

Validation also handles:

- Empty batches
- Uneven batch sizes
- Invalid batch structures

This allows raw telemetry data to be received in an array-oriented format and then converted into a more flexible collection structure for later analysis.

---

# Recursive Deployment Validation

Smart-X models sensor deployment structures as hierarchical trees.

A deployment may contain nested child locations, allowing Smart-X to represent structures such as:

```text
Facility
├── Zone A
│   ├── Sensor Node 1
│   └── Sensor Node 2
│
└── Zone B
    └── Sensor Node 3
```

Recursive processing is used to traverse and validate the deployment hierarchy.

This demonstrates recursion using a meaningful Smart-X use case rather than using recursion only for a mathematical example.

The recursive deployment implementation allows nested deployment structures to be processed regardless of their depth while keeping the tree model separated from the API and infrastructure layers.

---

# Secure Sensor Attachments

Smart-X supports files associated with individual sensor profiles.

Supported attachment categories are:

- Device configuration files
- Deployment photos
- Hardware log files

The attachment implementation is separated across the existing Smart-X architecture:

```text
Multipart HTTP Request
        |
        v
SmartX.Api
        |
        v
SensorAttachmentService
        |
        v
ISensorAttachmentStore
        |
        v
Encrypted Filesystem Storage
```

---

## Supported Attachment Types

### Configuration Files

Examples include:

```text
.json
.yaml
.yml
.cfg
.txt
```

### Deployment Photos

Examples include:

```text
.jpg
.jpeg
.png
.webp
```

### Hardware Logs

Examples include:

```text
.log
.txt
.json
.csv
```

Both file extensions and media types are validated before storage.

---

## Streaming Uploads

Sensor attachments are uploaded using:

```text
multipart/form-data
```

The API processes multipart requests directly from the HTTP request stream.

Smart-X does not require the complete uploaded file to be loaded into memory before processing.

The upload pipeline is:

```text
HTTP Request Body
        |
        v
MultipartReader
        |
        v
Size-Limited Stream
        |
        v
AES Encryption
        |
        v
Encrypted Storage
```

This makes the upload mechanism more appropriate for larger files than a design that copies complete uploads into memory first.

The maximum supported attachment size is:

```text
10 MiB
```

The size limit is enforced while the stream is being consumed rather than relying only on a client-provided `Content-Length` value.

---

# Attachment Security

Smart-X applies multiple controls before an uploaded attachment reaches persistent storage.

These include:

- Sensor profile ID validation
- Attachment category validation
- File-name validation
- Maximum file-name length
- Control-character rejection
- File-extension allowlists
- Media-type allowlists
- Maximum attachment size
- Path-separator rejection
- Generated internal storage identifiers
- Authenticated encryption

User-provided file names are treated only as metadata.

They are never used as physical filesystem paths.

For example, an uploaded file named:

```text
gateway.log
```

may be stored internally using a generated identifier similar to:

```text
68b4a84e00a7421782048f672df94c63.payload
```

This prevents user-controlled filenames from defining storage locations.

---

# Attachment Encryption

Attachment payloads are encrypted before being persisted to disk.

Smart-X uses:

```text
AES-256-GCM
```

AES-GCM provides both:

- Confidentiality
- Authentication

This means encrypted payloads cannot be read without the encryption key and modified ciphertext is detected during decryption.

Smart-X uses chunk-based encryption rather than loading an entire attachment into one encryption buffer.

Each encrypted chunk receives:

- A unique nonce
- AES-GCM ciphertext
- An authentication tag
- Authenticated associated data

A final authenticated record is also written so that incomplete or truncated encrypted files can be detected.

The resulting process is:

```text
Plaintext Stream
      |
      v
64 KiB Chunk
      |
      v
AES-256-GCM
      |
      ├── Ciphertext
      └── Authentication Tag
      |
      v
Encrypted Payload
```

Plaintext attachment payloads are not intentionally persisted by the backend.

Attachment metadata is stored separately from the encrypted payload.

---

# Attachment Encryption Key

The encryption key is intentionally excluded from source control.

Smart-X expects a Base64-encoded 32-byte encryption key through:

```text
Attachments__EncryptionKeyBase64
```

## macOS / Linux

Generate and configure a temporary development key:

```bash
export Attachments__EncryptionKeyBase64="$(openssl rand -base64 32)"
```

The variable applies to the current terminal session.

## Windows PowerShell

```powershell
$bytes = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
$env:Attachments__EncryptionKeyBase64 = [Convert]::ToBase64String($bytes)
```

Never commit a production encryption key to:

```text
appsettings.json
appsettings.Development.json
.env
GitHub
```

---

# Attachment Storage

The default encrypted attachment storage location is:

```text
src/SmartX.Api/App_Data/attachments
```

Runtime attachment storage is excluded from Git.

Each sensor receives its own generated storage directory.

The physical storage contains:

```text
<attachment-id>.payload
<attachment-id>.metadata.json
```

The `.payload` file contains encrypted attachment content.

The `.metadata.json` file contains non-payload information required to identify and retrieve the attachment.

---

# Attachment API

## Upload Attachment

```http
POST /api/sensors/{sensorProfileId}/attachments?category={category}
Content-Type: multipart/form-data
```

Valid categories are:

```text
Configuration
DeploymentPhoto
HardwareLog
```

Example using `curl`:

```bash
curl -k \
  -F "file=@device.log;type=text/plain" \
  "https://localhost:<PORT>/api/sensors/<SENSOR-ID>/attachments?category=HardwareLog"
```

---

## List Sensor Attachments

```http
GET /api/sensors/{sensorProfileId}/attachments
```

This returns attachment metadata associated with the supplied sensor profile ID.

---

## Download Attachment

```http
GET /api/sensors/{sensorProfileId}/attachments/{attachmentId}
```

The encrypted payload is read from storage, authenticated, decrypted, and streamed back to the client.

The plaintext file is not first recreated as a temporary file on disk.

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

# Solution Structure

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
│
└── SmartX.sln
```

## Projects

### `SmartX.Domain`

Contains:

- Domain entities
- Value objects
- Sensor models
- Telemetry models
- Historical telemetry processing
- Deployment structures
- Recursive validation
- Domain rules

### `SmartX.Contracts`

Contains API request and response contracts shared across application boundaries.

### `SmartX.Application`

Contains:

- Application services
- Attachment models
- Attachment validation
- Storage abstractions
- Application-level use cases

### `SmartX.Infrastructure`

Contains infrastructure-specific implementations including:

- AES-256-GCM attachment encryption
- Streaming encryption and decryption
- Encrypted filesystem storage
- Attachment metadata persistence

### `SmartX.Api`

Contains:

- ASP.NET Core controllers
- HTTP configuration
- Dependency injection
- Multipart streaming
- API endpoints

### `SmartX.Client`

Contains the React and TypeScript frontend.

### Test Projects

```text
SmartX.Domain.Tests
SmartX.Application.Tests
SmartX.Infrastructure.Tests
SmartX.Api.IntegrationTests
```

The `docs` directory contains research, diagrams, and assessment evidence.

---

# Prerequisites

Install the following before running Smart-X:

- .NET 10 SDK
- Node.js 24 or later
- npm
- Git

OpenSSL is useful for generating local development encryption keys on macOS and Linux.

---

# Restore the .NET Solution

From the repository root:

```bash
dotnet restore SmartX.sln
```

---

# Build the Backend

```bash
dotnet build SmartX.sln
```

For a Release build:

```bash
dotnet build SmartX.sln \
  --configuration Release \
  --no-restore
```

---

# Run Backend Tests

Run the complete automated test suite:

```bash
dotnet test SmartX.sln
```

Run only the domain tests:

```bash
dotnet test tests/SmartX.Domain.Tests/SmartX.Domain.Tests.csproj
```

Run only the application tests:

```bash
dotnet test tests/SmartX.Application.Tests/SmartX.Application.Tests.csproj
```

Run only the infrastructure tests:

```bash
dotnet test tests/SmartX.Infrastructure.Tests/SmartX.Infrastructure.Tests.csproj
```

Run only the API integration tests:

```bash
dotnet test tests/SmartX.Api.IntegrationTests/SmartX.Api.IntegrationTests.csproj
```

---

# Run the API

Attachment functionality requires an encryption key.

On macOS or Linux:

```bash
export Attachments__EncryptionKeyBase64="$(openssl rand -base64 32)"
```

Then run:

```bash
dotnet run --project src/SmartX.Api
```

The API starts using the development configuration defined by the project.

---

# Install Frontend Dependencies

```bash
cd src/SmartX.Client
npm ci
```

For development environments where the lock file does not need to be strictly reproduced:

```bash
npm install
```

---

# Run the Frontend

From:

```text
src/SmartX.Client
```

run:

```bash
npm run dev
```

Vite will start the React development server.

---

# Frontend Quality Checks

From `src/SmartX.Client`:

```bash
npm audit --audit-level=high
npm run lint
npm run format:check
npm run build
```

These commands verify:

- Dependency security
- ESLint rules
- Prettier formatting
- TypeScript compilation
- Production Vite build

---

# .NET Quality Checks

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

# Automated Testing

Smart-X currently uses four dedicated automated test projects:

```text
SmartX.Domain.Tests
SmartX.Application.Tests
SmartX.Infrastructure.Tests
SmartX.Api.IntegrationTests
```

At the end of Issue #9, the complete solution contains:

```text
104 automated tests
```

The tests cover areas including:

- Sensor profile validation
- Device identifiers
- Deployment locations
- Generic telemetry
- Numeric telemetry
- Operator overloading
- Historical telemetry batch processing
- Array-to-collection conversion
- Recursive deployment validation
- Attachment validation
- Attachment size limits
- Path traversal prevention
- AES encryption and decryption
- Ciphertext tamper detection
- Ciphertext truncation detection
- Encrypted filesystem storage
- Sensor-specific attachment metadata
- Multipart attachment uploads
- Attachment listing
- Secure attachment downloads

Run:

```bash
dotnet test SmartX.sln
```

to verify the current pass, failure, and skipped test totals.

---

# Continuous Integration

GitHub Actions automatically validates changes pushed to the repository.

The CI pipeline helps prevent regressions before feature branches are merged into the integration branch.

The project also performs local quality checks before pull requests are completed, including:

- Dependency restore and audit
- .NET formatting validation
- Release build
- Automated .NET tests
- Frontend dependency audit
- ESLint
- Prettier
- React production build
- Git whitespace validation

---

# Git Workflow

Development is completed using short-lived feature branches.

Example:

```text
feature/9-secure-sensor-attachments
```

Changes are committed in logical units and merged into the `dev` integration branch through pull requests.

The `dev` branch is used to integrate completed features before final delivery.

---

# Remaining Part 1 Work

The main remaining Part 1 work will extend the existing foundation with:

- Dynamic anomaly engagement functionality
- Remaining application and API integration
- React dashboard integration
- Final end-to-end testing
- Final assessment documentation
- Final Part 1 regression testing

---

# Future Smart-X Functionality

Later stages of the Portfolio of Evidence will build on the Part 1 foundation.

The completed Smart-X ecosystem will eventually support:

- Large-scale sensor telemetry processing
- Real-time device monitoring
- Command processing
- Device history
- Anomaly prioritisation
- IoT deployment hierarchies
- Mesh-network topology modelling
- Device routing
- Network optimisation
