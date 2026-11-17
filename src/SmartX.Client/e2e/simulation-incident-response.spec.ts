import { expect, test } from '@playwright/test';

test.describe('guided incident response', () => {
  test('does not expose acknowledgement controls without an actual telemetry trace', async ({
    page,
  }) => {
    let acknowledgements = 0;
    page.on('request', (request) => {
      if (
        request.method() === 'POST' &&
        /\/api\/dashboard\/issues\/.+\/acknowledge$/.test(request.url())
      ) {
        acknowledgements += 1;
      }
    });

    await page.goto('/simulation');

    await expect(page.getByRole('heading', { name: 'Review an actual incident' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Refresh live incidents' })).toBeDisabled();
    await expect(
      page.getByText(/Complete a telemetry trace on the healthy greenhouse fixture/),
    ).toBeVisible();
    await expect(page.getByRole('button', { name: 'Confirm acknowledgement' })).toHaveCount(0);
    await expect(page.getByRole('link', { name: 'Real-time Operations' })).toBeVisible();
    expect(acknowledgements).toBe(0);
  });
});
