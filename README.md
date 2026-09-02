# Smart-X IoT Mesh Ecosystem

**PROG7312 Portfolio of Evidence - 2026**

Smart-X is a simulated Internet of Things telemetry management ecosystem developed for the PROG7312 Portfolio of Evidence.

The completed system will provide sensor registration, telemetry ingestion, real-time command processing, device history, anomaly management, and IoT mesh-network routing.

## Student Information

- Student: James Maherry
- Student number: ST10437403
- Module: Programming 3B
- Module code: PROG7312

## Part 1

Part 1 focuses on the Smart-X Data Ingestion and Validation Gateway.

The implementation will include:

- A responsive React and TypeScript dashboard
- An ASP.NET Core Web API
- Sensor registration and telemetry ingestion
- Generic telemetry packets
- Operator-overloaded sensor value models
- Jagged or multidimensional telemetry arrays
- Optimised generic collections
- Recursive deployment hierarchy validation
- Encrypted device media and log attachments
- A dynamic anomaly engagement feature

## Technology Stack

### Backend

- C# and .NET 10
- ASP.NET Core Web API Controllers
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
- Git and GitHub
- GitHub Actions
- Docker and Docker Compose

## Solution Structure

- `src/SmartX.Domain` - Domain models, value objects, generics and data structures
- `src/SmartX.Contracts` - API request and response contracts
- `src/SmartX.Application` - Application services and validation
- `src/SmartX.Infrastructure` - Storage and infrastructure implementations
- `src/SmartX.Api` - ASP.NET Core Web API
- `src/SmartX.Client` - React and TypeScript dashboard
- `tests/SmartX.Domain.Tests` - Domain unit tests
- `tests/SmartX.Application.Tests` - Application unit tests
- `tests/SmartX.Api.IntegrationTests` - API integration tests
- `docs` - Research, diagrams and assessment evidence

## Prerequisites

- .NET 10 SDK
- Node.js 24 LTS
- npm
- Git
- Visual Studio Code

## Restore and Build the Backend

```bash
dotnet restore SmartX.sln
dotnet build SmartX.sln