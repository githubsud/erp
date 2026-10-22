import { expect, test } from '@playwright/test';
import { shop, today } from './support/api';
import { signIn, t } from './support/app';

test('last month\'s payroll: create, calculate, approve, post and download the WPS salary file', async ({ page }) => {
  const employee = shop().employee;
  page.on('dialog', (d) => void d.accept());
  await signIn(page);
  await page.goto('/payroll');

  const now = new Date(today());
  const last = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - 1, 1)).toISOString().slice(0, 7);
  await page.getByLabel(t('payroll.month')).fill(last);
  await page.getByRole('button', { name: t('payroll.new') }).click();
  await expect(page).toHaveURL(/\/payroll\/[0-9a-f-]{36}$/);

  await page.getByRole('button', { name: t('payroll.calculate') }).click();
  // A full month of basic 3,000 + housing 1,000.
  await expect(page.locator('tr', { hasText: employee.empNo })).toContainText('4,000.00');

  await page.getByRole('button', { name: t('payroll.approve') }).click();
  await page.getByRole('button', { name: t('payroll.post') }).click();

  const download = page.waitForEvent('download', { timeout: 30_000 });
  await page.getByRole('button', { name: t('payroll.generateWps') }).click();
  const file = await download;
  expect(file.suggestedFilename()).toMatch(/\.(sif|csv)$/i);
  const content = await (await file.createReadStream()).toArray();
  expect(Buffer.concat(content).toString('utf8')).toContain('28435612345');
});
