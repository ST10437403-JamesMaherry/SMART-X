import type { TelemetryDataType } from '../../api/sensorsApi';
import type { IngestTelemetryBatchRequest, IngestTelemetryRequest } from '../../api/telemetryApi';

/**
 * .NET Int32 boundaries mirrored by the frontend before Integer telemetry is
 * sent to the Smart-X API.
 */
const minimumInt32Value = -2_147_483_648;
const maximumInt32Value = 2_147_483_647;

/**
 * Accepts ordinary decimal and scientific-notation numeric values without
 * allowing JavaScript-specific numeric syntax such as hexadecimal literals.
 */
const decimalPattern = /^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?$/;

const integerPattern = /^[+-]?\d+$/;

/**
 * One editable row in the historical jagged-batch simulator.
 *
 * The stable ID is a UI concern only. Batch ordering is determined entirely
 * by the row's position in the array supplied to the backend.
 */
export interface TelemetryBatchRow {
  readonly id: string;
  readonly rawValues: string;
}

type TelemetryBatchValue = number | boolean;

/**
 * One successfully parsed batch.
 */
export interface ParsedTelemetryBatch {
  readonly rowId: string;
  readonly values: readonly TelemetryBatchValue[];
}

/**
 * Complete result of parsing the current jagged batch editor.
 */
export interface TelemetryBatchParseResult {
  readonly batches: readonly ParsedTelemetryBatch[];
  readonly errors: Readonly<Record<string, string>>;
  readonly packetCount: number;
}

/**
 * Creates a stable row identifier for React rendering and accessible field
 * association.
 */
function createRowId(): string {
  return crypto.randomUUID();
}

/**
 * Creates one empty batch row.
 */
export function createTelemetryBatchRow(rawValues = ''): TelemetryBatchRow {
  return {
    id: createRowId(),
    rawValues,
  };
}

/**
 * Creates the initial editor state.
 *
 * One empty inner batch is valid because the backend deliberately preserves
 * empty batches as part of the original historical sequence.
 */
export function createInitialTelemetryBatchRows(): readonly TelemetryBatchRow[] {
  return [createTelemetryBatchRow()];
}

/**
 * Creates a deliberately uneven sample structure.
 *
 * The resulting batch lengths are:
 *
 * [2, 1, 0, 3]
 *
 * This provides an immediate demonstration that Smart-X accepts a jagged
 * structure rather than assuming every historical batch has equal length.
 */
export function createSampleTelemetryBatchRows(
  telemetryType: TelemetryDataType,
): readonly TelemetryBatchRow[] {
  if (telemetryType === 'Float') {
    return [
      createTelemetryBatchRow('21.4, 21.8'),
      createTelemetryBatchRow('22.1'),
      createTelemetryBatchRow(''),
      createTelemetryBatchRow('22.4, 22.7, 23.0'),
    ];
  }

  if (telemetryType === 'Integer') {
    return [
      createTelemetryBatchRow('4100, 4175'),
      createTelemetryBatchRow('4220'),
      createTelemetryBatchRow(''),
      createTelemetryBatchRow('4190, 4250, 4310'),
    ];
  }

  return [
    createTelemetryBatchRow('true, false'),
    createTelemetryBatchRow('true'),
    createTelemetryBatchRow(''),
    createTelemetryBatchRow('false, false, true'),
  ];
}

/**
 * Parses one comma-separated batch.
 *
 * A completely empty row represents a legitimate empty inner batch. However,
 * empty positions inside a non-empty row such as "1,,3" are treated as input
 * mistakes rather than silently discarded.
 */
function parseBatchValues(
  rawValues: string,
  telemetryType: TelemetryDataType,
): {
  readonly values?: readonly TelemetryBatchValue[];
  readonly error?: string;
} {
  const normalizedValues = rawValues.trim();

  if (!normalizedValues) {
    return {
      values: [],
    };
  }

  const tokens = normalizedValues.split(',').map((token) => token.trim());

  const emptyTokenIndex = tokens.findIndex((token) => token.length === 0);

  if (emptyTokenIndex >= 0) {
    return {
      error:
        `Value ${emptyTokenIndex + 1} is empty. ` + 'Remove the extra comma or provide a value.',
    };
  }

  if (telemetryType === 'Boolean') {
    const booleanValues: boolean[] = [];

    for (let tokenIndex = 0; tokenIndex < tokens.length; tokenIndex++) {
      const normalizedToken = tokens[tokenIndex].toLowerCase();

      if (normalizedToken !== 'true' && normalizedToken !== 'false') {
        return {
          error: `Value ${tokenIndex + 1} must be either true or false.`,
        };
      }

      booleanValues.push(normalizedToken === 'true');
    }

    return {
      values: booleanValues,
    };
  }

  if (telemetryType === 'Integer') {
    const integerValues: number[] = [];

    for (let tokenIndex = 0; tokenIndex < tokens.length; tokenIndex++) {
      const token = tokens[tokenIndex];

      if (!integerPattern.test(token)) {
        return {
          error: `Value ${tokenIndex + 1} must be a whole number.`,
        };
      }

      const parsedValue = Number(token);

      if (
        !Number.isSafeInteger(parsedValue) ||
        parsedValue < minimumInt32Value ||
        parsedValue > maximumInt32Value
      ) {
        return {
          error: `Value ${tokenIndex + 1} must fit within the .NET Int32 range.`,
        };
      }

      integerValues.push(parsedValue);
    }

    return {
      values: integerValues,
    };
  }

  const floatValues: number[] = [];

  for (let tokenIndex = 0; tokenIndex < tokens.length; tokenIndex++) {
    const token = tokens[tokenIndex];

    if (!decimalPattern.test(token)) {
      return {
        error: `Value ${tokenIndex + 1} must be a valid decimal number.`,
      };
    }

    const parsedValue = Number(token);

    if (!Number.isFinite(parsedValue)) {
      return {
        error: `Value ${tokenIndex + 1} must be a finite decimal number.`,
      };
    }

    floatValues.push(parsedValue);
  }

  return {
    values: floatValues,
  };
}

/**
 * Parses every visible editor row while preserving exact outer-array order.
 */
export function parseTelemetryBatchRows(
  rows: readonly TelemetryBatchRow[],
  telemetryType: TelemetryDataType,
): TelemetryBatchParseResult {
  const batches: ParsedTelemetryBatch[] = [];
  const errors: Record<string, string> = {};

  let packetCount = 0;

  for (const row of rows) {
    const parsedBatch = parseBatchValues(row.rawValues, telemetryType);

    if (parsedBatch.error) {
      errors[row.id] = parsedBatch.error;

      batches.push({
        rowId: row.id,
        values: [],
      });

      continue;
    }

    const values = parsedBatch.values ?? [];

    batches.push({
      rowId: row.id,
      values,
    });

    packetCount += values.length;
  }

  return {
    batches,
    errors,
    packetCount,
  };
}

/**
 * Returns true when one or more batch rows contain invalid values.
 */
export function hasTelemetryBatchErrors(result: TelemetryBatchParseResult): boolean {
  return Object.keys(result.errors).length > 0;
}

/**
 * Converts one validated primitive value into the exact strongly typed HTTP
 * contract required by the selected sensor.
 */
function createTelemetryRequest(
  telemetryType: TelemetryDataType,
  value: TelemetryBatchValue,
  capturedAtUtc: string,
): IngestTelemetryRequest {
  if (telemetryType === 'Boolean') {
    if (typeof value !== 'boolean') {
      throw new Error('Boolean telemetry requires a Boolean batch value.');
    }

    return {
      capturedAtUtc,
      floatValue: null,
      integerValue: null,
      booleanValue: value,
    };
  }

  if (typeof value !== 'number') {
    throw new Error('Numeric telemetry requires a numeric batch value.');
  }

  if (telemetryType === 'Integer') {
    return {
      capturedAtUtc,
      floatValue: null,
      integerValue: value,
      booleanValue: null,
    };
  }

  return {
    capturedAtUtc,
    floatValue: value,
    integerValue: null,
    booleanValue: null,
  };
}

/**
 * Converts parsed UI batches into the jagged transport structure consumed by
 * POST /api/sensors/{id}/telemetry/batches.
 *
 * Capture timestamps are generated in flattened sequence order, ending at the
 * current time. This keeps packets chronological while preserving both outer
 * batch order and inner sequence order.
 */
export function toTelemetryBatchRequest(
  result: TelemetryBatchParseResult,
  telemetryType: TelemetryDataType,
): IngestTelemetryBatchRequest {
  const totalPacketCount = result.packetCount;

  const firstCaptureTime = Date.now() - Math.max(totalPacketCount - 1, 0) * 1000;

  let flattenedSequenceIndex = 0;

  const batches = result.batches.map((batch) =>
    batch.values.map((value) => {
      const capturedAtUtc = new Date(
        firstCaptureTime + flattenedSequenceIndex * 1000,
      ).toISOString();

      flattenedSequenceIndex++;

      return createTelemetryRequest(telemetryType, value, capturedAtUtc);
    }),
  );

  return {
    batches,
  };
}
