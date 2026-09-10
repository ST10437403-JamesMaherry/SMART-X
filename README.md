# Smart-X IoT Mesh Ecosystem

**PROG7312 Portfolio of Evidence - 2026**

Smart-X is a simulated Internet of Things (IoT) telemetry management system developed for the PROG7312 Portfolio of Evidence.

The system is designed to register IoT sensors, receive strongly typed telemetry readings, monitor device behaviour, detect important changes, and eventually manage a simulated IoT mesh network.

---

## Student Information

- **Student:** James Maherry
- **Student Number:** ST10437403
- **Module:** Programming 3B
- **Module Code:** PROG7312

---

## What Smart-X Does

Smart-X acts as a central gateway for IoT devices.

For example, sensors may report:

- Temperature readings
- Power consumption
- Moisture levels
- Valve or actuator states

Smart-X identifies which sensor produced a reading, validates the information, processes the telemetry, and prepares it for monitoring and analysis.

The completed system will include sensor management, telemetry processing, anomaly detection, device history, command processing, encrypted attachments, and IoT mesh-network routing.

---

## Part 1

Part 1 focuses on building the **Smart-X Data Ingestion and Validation Gateway**.

The completed Part 1 system will include:

- Responsive React and TypeScript dashboard
- ASP.NET Core Web API
- Sensor registration and management
- Strongly typed telemetry ingestion
- Generic telemetry packets
- Meaningful operator overloading
- Optimised generic collections
- Jagged or multidimensional telemetry arrays
- Recursive deployment hierarchy validation
- Encrypted device media and log attachments
- Dynamic anomaly engagement functionality

---

## Current Progress

The project currently includes:

- Clean multi-project .NET solution architecture
- ASP.NET Core Web API foundation
- React and TypeScript frontend
- Automated GitHub Actions CI
- Sensor domain model
- Sensor profiles with validation
- MAC address and custom device identifiers
- Structured deployment locations
- Sensor categories and operational statuses
- Telemetry data type definitions
- Domain-specific validation exceptions
- UTC creation and update timestamps
- Generic `TelemetryPacket<T>` implementation
- Strongly typed `double`, `int`, and `bool` telemetry
- Numeric telemetry support using .NET generic mathematics
- `TelemetryDelta<T>` for comparing sensor readings
- Meaningful overloaded subtraction operator for telemetry delta calculation
- Validation for invalid telemetry operations
- Comprehensive automated domain tests

At the end of Issue #6, the full solution contains:

**63 passing automated tests with 0 failures.**

---

## Sensor Domain Model

Each registered Smart-X sensor has a domain profile containing information such as:

- Sensor name
- Device MAC address or unique identifier
- Deployment location
- Sensor category
- Sensor status
- Expected telemetry data type
- Creation timestamp
- Update timestamp

The domain model validates this information before it can be used elsewhere in the application.

Device identifiers support both standard MAC addresses and custom Smart-X identifiers.

Deployment locations are represented using a hierarchy containing:

```text
Facility
└── Zone
    └── Node
```

This structure will also support later recursive deployment validation.

---

## Generic Telemetry

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

This allows different sensors to use the same telemetry model while keeping their real data types.

For example:

```text
Temperature sensor  -> double
Power counter       -> int
Valve state         -> bool
```

Telemetry values are not stored as `object`, avoiding unnecessary boxing, casting, and runtime type conversion.

Each telemetry packet also contains:

- Source device identifier
- Strongly typed payload
- UTC capture timestamp

---

## Numeric Telemetry and Operator Overloading

Numeric sensor readings use:

```csharp
NumericTelemetryPacket<T>
```

This supports numeric telemetry such as:

```csharp
NumericTelemetryPacket<double>
NumericTelemetryPacket<int>
```

Smart-X overloads the subtraction operator so that two readings from the same sensor can be compared directly.

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

A telemetry delta contains:

- Previous value
- Current value
- Signed delta
- Absolute magnitude
- Elapsed time
- Increase state
- Decrease state
- Unchanged state

This operator has a real purpose within Smart-X because it prepares the system for later features such as:

- Spike detection
- Sudden drop detection
- Anomaly detection
- Rate-of-change analysis
- Threshold monitoring

Boolean telemetry is still supported through `TelemetryPacket<bool>`, but boolean values are intentionally excluded from numeric subtraction because subtracting boolean states has no meaningful purpose.

---

## Technology Stack

### Backend

- C#
- .NET 10
- ASP.NET Core Web API
- OpenAPI
- xUnit

### Frontend

- React
- TypeScript
- Vite
- ESLint
- Prettier

### Development and Delivery

- Visual Studio Code
- Git
- GitHub
- GitHub Actions

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
│   └── SmartX.Api.IntegrationTests/
│
├── docs/
└── SmartX.sln
```

### Projects

- `SmartX.Domain` - Domain entities, value objects, telemetry models and business rules
- `SmartX.Contracts` - API request and response contracts
- `SmartX.Application` - Application services and use cases
- `SmartX.Infrastructure` - Storage and infrastructure implementations
- `SmartX.Api` - ASP.NET Core Web API
- `SmartX.Client` - React and TypeScript frontend
- `SmartX.Domain.Tests` - Domain unit tests
- `SmartX.Application.Tests` - Application unit tests
- `SmartX.Api.IntegrationTests` - API integration tests
- `docs` - Research, diagrams and assessment evidence

---

## Prerequisites

Install the following before running Smart-X:

- .NET 10 SDK
- Node.js 24 or later
- npm
- Git

---

## Restore the .NET Solution

From the repository root:

```bash
dotnet restore SmartX.sln
```

---

## Build the Backend

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

## Run Backend Tests

Run the complete automated test suite:

```bash
dotnet test SmartX.sln
```

Run only the domain tests:

```bash
dotnet test tests/SmartX.Domain.Tests/SmartX.Domain.Tests.csproj
```

Run the Release test suite:

```bash
dotnet test SmartX.sln \
  --configuration Release \
  --no-build
```

---

## Run the API

From the repository root:

```bash
dotnet run --project src/SmartX.Api
```

The API will start using the development configuration defined by the project.

---

## Install Frontend Dependencies

```bash
cd src/SmartX.Client
npm install
```

For a clean dependency installation using the existing lock file:

```bash
npm ci
```

---

## Run the Frontend

From `src/SmartX.Client`:

```bash
npm run dev
```

Vite will start the React development server.

---

## Frontend Quality Checks

From `src/SmartX.Client`:

```bash
npm run format:check
npm run lint
npm run build
```

These commands verify:

- Prettier formatting
- ESLint rules
- TypeScript compilation
- Production Vite build

---

## .NET Quality Checks

From the repository root:

```bash
dotnet restore SmartX.sln

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

## Automated Testing

Smart-X currently uses three automated test projects:

```text
SmartX.Domain.Tests
SmartX.Application.Tests
SmartX.Api.IntegrationTests
```

The current solution contains:

```text
63 tests passed
0 tests failed
0 tests skipped
```

The test suite will continue to grow as new Smart-X functionality is implemented.

---

## Continuous Integration

GitHub Actions is used to automatically validate changes pushed to the repository.

The CI process helps ensure that changes do not break the existing Smart-X implementation before they are merged into the integration branch.

Local formatting, build, and test checks are also performed before pull requests are merged.

---

## Remaining Part 1 Work

Upcoming Smart-X features will extend the current foundation with:

- Sensor registration application services
- Sensor registration API endpoints
- Telemetry ingestion and processing
- Generic telemetry collections
- Optimised collection usage
- Jagged or multidimensional telemetry arrays
- Recursive deployment hierarchy validation
- Device media and log attachments
- AES encryption for uploaded files
- Dynamic anomaly engagement functionality
- Additional API integration
- React dashboard integration
- Final testing and documentation

---

## Future Smart-X Functionality

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
