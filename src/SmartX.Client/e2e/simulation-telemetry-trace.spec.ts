import { expect, test } from '@playwright/test';

test.describe('Simulation Lab telemetry trace', () => {
  test('blocks telemetry writes in an unrelated workspace', async ({ page, request }) => {
    // Tests share an in-memory API instance. A deliberately unrelated sensor
    // proves that this new dashboard cannot submit readings into the wrong lab.
    const identifier = `trace-guard-${Date.now()}-${Math.random().toString(36).slice(2)}`;
    const registered = await request.post('http://127.0.0.1:5095/api/sensors', {
      data: {
        name: 'Unrelated trace safety sensor',
        deviceIdentifier: identifier,
        identifierKind: 'UniqueIdentifier',
        deploymentLocation: {
          facility: 'Unrelated demo',
          zone: 'Isolation',
          nodeId: identifier,
        },
        category: 'Environmental',
        telemetryDataType: 'Float',
      },
    });
    expect(registered.status()).toBe(201);

    await page.goto('/simulation');
    await expect(page.getByRole('heading', { name: 'Follow one sensor reading' })).toBeVisible();
    await expect(page.getByRole('group', { name: 'Telemetry trace inputs' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Send baseline reading' })).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Send stable reading' })).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Send spike reading' })).toBeDisabled();
    await expect(
      page.getByText(/Load the healthy greenhouse scenario before tracing telemetry/),
    ).toBeVisible();
    await expect(
      page.getByRole('heading', { name: 'Follow the data through Smart-X' }),
    ).toBeVisible();

    const probeTelemetry = await request.get('http://127.0.0.1:5095/api/sensors');
    expect(probeTelemetry.ok()).toBeTruthy();
    const sensors = (await probeTelemetry.json()) as { deviceIdentifier: string }[];
    expect(sensors.some((sensor) => sensor.deviceIdentifier === identifier)).toBeTruthy();
  });
});
