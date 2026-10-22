import { join } from 'node:path';
import { expect, test } from '@playwright/test';
import { Api, shop } from './support/api';
import { signIn, t } from './support/app';

test('a price list from Excel is checked first, then imported: new products added and an existing price updated', async ({ page }) => {
  const milk = shop().items[0];
  await signIn(page);
  await page.goto('/inventory/items');
  await page.getByRole('button', { name: t('xl.button') }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('heading', { name: t('xl.items') })).toBeVisible();

  // The template downloads as an Excel file.
  const download = page.waitForEvent('download', { timeout: 30_000 });
  await dialog.getByRole('button', { name: t('xl.template') }).click();
  expect((await download).suggestedFilename()).toMatch(/\.xlsx$/);

  await dialog.locator('input[type=file]').setInputFiles(join(__dirname, 'fixtures', 'price-list.xlsx'));
  await expect(dialog.getByText('price-list.xlsx')).toBeVisible();
  await dialog.getByRole('button', { name: t('xl.check') }).click();
  await expect(dialog).toContainText(t('xl.allGood', { n: 3, created: 2, updated: 1 }), { timeout: 30_000 });

  await dialog.getByRole('button', { name: t('xl.import'), exact: true }).click();
  await expect(dialog).toContainText(t('xl.done', { created: 2, updated: 1 }), { timeout: 30_000 });
  await dialog.getByRole('button', { name: t('app.close') }).click();

  // Sugar arrives with its opening stock (40 at 7.00); milk keeps its stock and gets the new price.
  await expect(page.locator('tr', { hasText: 'سكر 2 كغ' })).toContainText('280.00');
  await expect(page.locator('tr', { hasText: milk.nameAr })).toContainText('100');
  const s = shop();
  const api = await Api.signIn(new URL(page.url()).origin, s.code, s.owner.email, s.owner.password);
  const items = await api.get<{ items: { sku: string; salesPrice: number }[] }>('inventory/items');
  expect(items.items.find((i) => i.sku === milk.sku)?.salesPrice).toBe(7);
});
