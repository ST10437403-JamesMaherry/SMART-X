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

For example, sensors could report:

- Temperature readings
- Power consumption
- Moisture levels
- Valve or actuator states

Smart-X identifies which sensor produced the reading, validates the information, processes the telemetry, and prepares it for monitoring and analysis.

The completed system will include sensor management, telemetry processing, anomaly detection, device history, command processing, and mesh-network routing.

---

## Current Progress

The project currently includes:

- Clean multi-project .NET solution architecture
- ASP.NET Core Web API
- React and TypeScript frontend
- Automated GitHub Actions CI
- Sensor domain model
- Sensor profiles with validation
- MAC address and custom device identifiers
- Structured deployment locations
- Sensor categories and operational statuses
- Telemetry data type definitions
- Domain-specific exceptions
- UTC creation and update timestamps
- Generic `TelemetryPacket<T>` implementation
- Strongly typed `double`, `int`, and `bool` telemetry
- Numeric telemetry support using .NET generic mathematics
- `TelemetryDelta<T>` for comparing sensor readings
- Meaningful overloaded `-` operator for telemetry delta calculation
- Validation for invalid telemetry operations
- Comprehensive automated tests

At the end of Issue #6, the full solution contains:

**63 passing automated tests with 0 failures.**

---

## Generic Telemetry

Smart-X keeps sensor readings strongly typed using:

```csharp
TelemetryPacket<T>
