import { expect, test } from '@playwright/test';

test.describe('guided Simulation Lab', () => {
  test('makes the guided experience discoverable from the home screen', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Simulation Lab' })).toBeVisible();
    await page.getByRole('link', { name: /Open the guided simulation/ }).click();
    await expect(page).toHaveURL(/\/simulation$/);
    await expect(page.getByRole('heading', { level: 1, name: 'Simulation Lab' })).toBeVisible();
    await expect(page.getByRole('heading', { name: "Choose what happens" })).toBeVisible();
  });

  test('presents four scenarios and authoritative empty-state metrics', async ({ page }) => {
    await page.goto('/simulation');
    await expect(page.getByRole('button', { name: /Run 01 \/ Healthy network/ })).toBeVisible();
    await expect(page.getByRole('button', { name: /Run 02 \/ Primary relay fails/ })).toBeVisible();
    await expect(page.getByRole('button', { name: /Run 03 \/ Gateway partition/ })).toBeVisible();
    await expect(page.getByRole('button', { name: /Run 04 \/ Recover network/ })).toBeVisible();
    await expect(page.getByRole('heading', { name: "What's happening in the network?" })).toBeVisible();
    await expect(page.getByRole('group', { name: 'Graph overlay mode' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'What changed?' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Follow the data through Smart-X' })).toBeVisible();
  });

  test('refuses to modify a workspace with an unrelated real sensor', async ({ page, request }) => {
    const id = `simulation-guard-${Date.now()}-${Math.random().toString(36).slice(2)}`;
    const registration = await request.post('http://127.0.0.1:5095/api/sensors', {
      data: {
        name: 'Keep my operator sensor',
        deviceIdentifier: id,
        identifierKind: 'UniqueIdentifier',
        deploymentLocation: { facility: 'Operator lab', zone: 'Safety', nodeId: id },
        category: 'Environmental',
        telemetryDataType: 'Float',
      },
    });
    expect(registration.status()).toBe(201);
    await page.goto('/simulation');
    await page.getByRole('button', { name: /Run 01 \/ Healthy network/ }).click();
    await expect(page.getByTestId('simulation-feedback')).toContainText(
      'This workspace contains other devices or modified demo sensors',
    );
    const registered = await request.get('http://127.0.0.1:5095/api/sensors');
    expect(registered.ok()).toBeTruthy();
    const all = (await registered.json()) as { deviceIdentifier: string }[];
    expect(all.some((sensor) => sensor.deviceIdentifier === id)).toBeTruthy();
    expect(all.some((sensor) => sensor.deviceIdentifier === 'AA:BB:CC:EE:07:A1')).toBeFalsy();
  });
});
