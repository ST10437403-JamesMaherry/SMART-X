import { randomUUID } from 'node:crypto';

import { expect, test, type APIRequestContext } from '@playwright/test';

/**
 * P2-11 release regression against the actual in-memory .NET API.
 *
 * The existing Part 1 and Part 2 test projects cover domain-specific edge
 * cases. This small browser-runner API test closes a cross-feature gap:
 * a single mixed-type, jagged-batch load must be both accepted and fully
 * processed, without truncating the independent SortedDictionary history.
 *
 * Intentionally 3 x 12 instead of 100 x 100 in CI. The standalone verifier
 * under scripts/ can run the same invariant at the full 10,000-packet size.
 */
const apiBaseUrl = 'http://127.0.0.1:5095';
const readingsPerDevice = 12;
const payloadTypes = [
  { type: 'Float', category: 'Environmental' },
  { type: 'Integer', category: 'PowerConsumption' },
  { type: 'Boolean', category: 'Actuator' },
] as const;

type PayloadType = (typeof payloadTypes)[number]['type'];

interface ProcessingSnapshot {
  readonly acceptedCount: number;
  readonly processedCount: number;
  readonly pendingCount: number;
  readonly outstandingDispatchedCount: number;
  readonly ordinaryDispatchPaused: boolean;
  readonly workerStatus: string;
  readonly failure: string | null;
}

interface IngestReading {
  readonly capturedAtUtc: string;
  readonly floatValue: number | null;
  readonly integerValue: number | null;
  readonly booleanValue: boolean | null;
}

interface HistorySnapshot {
  readonly totalRecordedEvents: number;
  readonly events: readonly {
    readonly eventId: string;
    readonly kind: string;
    readonly occurredAtUtc: string;
    readonly telemetry: (IngestReading & { readonly telemetryDataType: PayloadType }) | null;
  }[];
}

async function getJson<T>(request: APIRequestContext, path: string): Promise<T> {
  const response = await request.get(`${apiBaseUrl}${path}`);
  expect(response.status(), `GET ${path}`).toBe(200);
  return (await response.json()) as T;
}

/** Same UTC timestamp at indices 5 and 6 proves equal-time preservation. */
function capturedAt(baseMs: number, index: number): string {
  const seconds = index === 6 ? 5 : index;
  return new Date(baseMs + seconds * 1_000).toISOString();
}

function reading(type: PayloadType, baseMs: number, index: number): IngestReading {
  return {
    capturedAtUtc: capturedAt(baseMs, index),
    floatValue: type === 'Float' ? 20 + index / 1_000 : null,
    integerValue: type === 'Integer' ? 1_000 + index : null,
    booleanValue: type === 'Boolean' ? index % 2 === 0 : null,
  };
}

function typedValue(readingValue: IngestReading, type: PayloadType): number | boolean | null {
  return type === 'Float'
    ? readingValue.floatValue
    : type === 'Integer'
      ? readingValue.integerValue
      : readingValue.booleanValue;
}

test.describe('P2-11 final mixed-type load and regression', () => {
  test('real jagged batches produce exact receipts, typed history and unique active conditions', async ({
    request,
  }) => {
    test.setTimeout(90_000);

    // The worker is shared by all Playwright tests: wait for prior work to
    // drain rather than assuming process-wide counters begin at zero.
    await expect
      .poll(
        async () => {
          const state = await getJson<ProcessingSnapshot>(request, '/api/realtime/processing');
          expect(state.failure).toBeNull();
          expect(state.workerStatus).not.toBe('Faulted');
          return state.pendingCount + state.outstandingDispatchedCount;
        },
        { timeout: 20_000, intervals: [100, 250, 500] },
      )
      .toBe(0);

    const before = await getJson<ProcessingSnapshot>(request, '/api/realtime/processing');
    expect(before.ordinaryDispatchPaused).toBe(false);

    const runId = randomUUID().slice(0, 12);
    const deviceIds: string[] = [];
    const originalReadings = new Map<string, readonly IngestReading[]>();
    let submitted = 0;
    let accepted = 0;

    for (const [deviceIndex, specification] of payloadTypes.entries()) {
      const name = `P2-11 ${specification.type} load ${runId}`;
      const registration = await request.post(`${apiBaseUrl}/api/sensors`, {
        data: {
          name,
          deviceIdentifier: `p211-${runId}-${deviceIndex}`,
          identifierKind: 'UniqueIdentifier',
          deploymentLocation: {
            facility: 'Smart-X CI',
            zone: 'P2-11 final regression',
            nodeId: `node-${runId}-${deviceIndex}`,
          },
          category: specification.category,
          telemetryDataType: specification.type,
        },
      });
      expect(registration.status(), `Register ${specification.type}`).toBe(201);
      const sensor = (await registration.json()) as { id: string };
      deviceIds.push(sensor.id);

      const online = await request.patch(`${apiBaseUrl}/api/sensors/${sensor.id}/status`, {
        data: { status: 'Online' },
      });
      expect(online.status(), `Set ${specification.type} Online`).toBe(200);

      // Deliberately leave out configuration attachment uploads: this checks
      // that MissingConfiguration issues do not duplicate in the HashSet.
      const baseMs = Date.now() - 120_000 - readingsPerDevice * 1_000;
      const packets = Array.from({ length: readingsPerDevice }, (_, index) =>
        reading(specification.type, baseMs, index),
      );
      originalReadings.set(sensor.id, packets);
      submitted += packets.length;

      // A genuine jagged request with unequal inner batches (7 and 5).
      const batchResponse = await request.post(
        `${apiBaseUrl}/api/sensors/${sensor.id}/telemetry/batches`,
        { data: { batches: [packets.slice(0, 7), packets.slice(7)] } },
      );
      expect(batchResponse.status(), `Ingest ${specification.type}`).toBe(201);
      const receipt = (await batchResponse.json()) as {
        sensorProfileId: string;
        telemetryDataType: PayloadType;
        batchCount: number;
        acceptedPacketCount: number;
      };
      expect(receipt).toEqual(
        expect.objectContaining({
          sensorProfileId: sensor.id,
          telemetryDataType: specification.type,
          batchCount: 2,
          acceptedPacketCount: readingsPerDevice,
        }),
      );
      accepted += receipt.acceptedPacketCount;
    }

    expect(submitted).toBe(36);
    expect(accepted).toBe(submitted);

    // Actual completed processing is verified independently of HTTP 201.
    // There are no arbitrary elapsed-time performance assertions.
    await expect
      .poll(
        async () => {
          const state = await getJson<ProcessingSnapshot>(request, '/api/realtime/processing');
          expect(state.failure).toBeNull();
          return state.processedCount - before.processedCount;
        },
        { timeout: 45_000, intervals: [100, 250, 500] },
      )
      .toBe(accepted);

    const after = await getJson<ProcessingSnapshot>(request, '/api/realtime/processing');
    expect(after.acceptedCount - before.acceptedCount).toBe(accepted);
    expect(after.processedCount - before.processedCount).toBe(accepted);
    expect(after.pendingCount).toBe(0);
    expect(after.outstandingDispatchedCount).toBe(0);
    expect(after.failure).toBeNull();

    for (const [index, sensorId] of deviceIds.entries()) {
      const type = payloadTypes[index].type;
      const originals = originalReadings.get(sensorId);
      expect(originals).toBeDefined();
      if (!originals) throw new Error('Missing test submission record.');

      // The Part 1 recent-history endpoint must still preserve ingestion
      // order, including Boolean false and duplicate capture timestamps.
      const partOne = await getJson<readonly IngestReading[]>(
        request,
        `/api/sensors/${sensorId}/telemetry?limit=50`,
      );
      expect(partOne.map((item) => typedValue(item, type))).toEqual(
        originals.map((item) => typedValue(item, type)),
      );
      // DateTimeOffset may serialize equivalent UTC instants with either a
      // Z suffix or +00:00; compare instants, not string formatting.
      expect(partOne.map((item) => Date.parse(item.capturedAtUtc))).toEqual(
        originals.map((item) => Date.parse(item.capturedAtUtc)),
      );

      // The independent SortedDictionary history must contain every worker
      // completion, not a snapshot of the bounded recent-dispatch window.
      const history = await getJson<HistorySnapshot>(
        request,
        `/api/realtime/devices/${sensorId}/history?limit=50&order=asc`,
      );
      expect(history.totalRecordedEvents).toBe(readingsPerDevice);
      expect(history.events).toHaveLength(readingsPerDevice);
      expect(new Set(history.events.map((event) => event.eventId)).size).toBe(readingsPerDevice);
      expect(history.events.every((event) => event.kind === 'TelemetryProcessed')).toBe(true);
      expect(history.events.every((event) => event.telemetry !== null)).toBe(true);
      expect(history.events.every((event) => event.telemetry?.telemetryDataType === type)).toBe(
        true,
      );

      const captureTimes = history.events.map((event) => Date.parse(event.occurredAtUtc));
      expect(captureTimes).toEqual([...captureTimes].sort((left, right) => left - right));
      const repeatedTime = originals[5].capturedAtUtc;
      expect(
        history.events.filter(
          (event) => Date.parse(event.telemetry!.capturedAtUtc) === Date.parse(repeatedTime),
        ),
      ).toHaveLength(2);

      // No worker item may disappear or become a different typed value.
      const actualValues = history.events.map((event) => typedValue(event.telemetry!, type));
      expect(actualValues).toEqual(originals.map((item) => typedValue(item, type)));
    }

    const active = await getJson<{
      activeConditions: readonly { membershipKey: string; sensorProfileId: string }[];
    }>(request, '/api/realtime/snapshot/active-conditions');
    const ownConditions = active.activeConditions.filter((item) =>
      deviceIds.includes(item.sensorProfileId),
    );
    expect(new Set(ownConditions.map((item) => item.membershipKey)).size).toBe(
      ownConditions.length,
    );
  });
});
