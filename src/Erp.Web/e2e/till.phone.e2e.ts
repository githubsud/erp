import { expect, Page, test } from '@playwright/test';
import { Api, shop } from './support/api';
import { signIn, t, toast } from './support/app';

/** Opens the till's shift (or carries on with the open one), as the cashier. */
async function startShift(page: Page): Promise<void> {
  await signIn(page, 'cashier');
  await page.goto('/sales/pos');
  const resume = page.getByRole('button', { name: t('pos.resume') });
  const open = page.getByRole('button', { name: t('pos.openShift') });
  await expect(resume.or(open).first()).toBeVisible({ timeout: 30_000 });
  if (await resume.isVisible()) {
    await resume.click();
  } else {
    await open.click();
    const dialog = page.getByRole('dialog');
    await dialog.getByLabel(t('pos.float')).fill('200');
    await dialog.getByRole('button', { name: t('pos.openShift') }).click();
  }
  await expect(page.getByRole('button', { name: t('pos.closeShift') })).toBeVisible();
}

async function scan(page: Page, sku: string): Promise<void> {
  const box = page.getByPlaceholder(t('pos.scan'));
  await box.fill(sku);
  await box.press('Enter');
}

test('a sale on the phone: scan, pay cash, refund one item, close the shift with a Z report', async ({ page }) => {
  // Tea and rice: the import test changes the milk price.
  const [, rice, tea] = shop().items;
  await startShift(page);

  await scan(page, tea.sku);
  await scan(page, tea.sku);
  await page.locator('.tile', { hasText: rice.nameAr }).click();
  const total = tea.price * 2 + rice.price;
  await expect(page.locator('.total')).toContainText(total.toFixed(2));
  // Everything fits the phone's width: nothing to scroll sideways to reach the total or the pay button.
  expect(await page.evaluate(() => { const m = document.querySelector('mat-sidenav-content')!; return m.scrollWidth - m.clientWidth; })).toBeLessThanOrEqual(1);

  await page.getByRole('button', { name: t('pos.exactCash') }).click();
  await page.getByRole('button', { name: t('pos.pay'), exact: true }).click();
  const receipt = page.locator('.last b.ltr');
  await expect(receipt).toHaveText(/\S+/);
  const receiptNo = (await receipt.textContent())!.trim();
  await expect(page.getByText(t('pos.emptyCart'))).toBeVisible();

  // The customer brings one tea back.
  await page.getByRole('button', { name: t('pos.refund') }).click();
  const refund = page.getByRole('dialog');
  await refund.getByLabel(t('pos.receipt')).fill(receiptNo);
  await refund.getByRole('button', { name: t('app.search') }).click();
  await refund.locator('tr', { hasText: tea.nameAr }).locator('input.qty').fill('1');
  await refund.getByRole('button', { name: t('pos.refund') }).click();
  await expect(refund).toBeHidden();

  // End of the day: count the drawer and close.
  await page.getByRole('button', { name: t('pos.closeShift') }).click();
  const close = page.getByRole('dialog');
  await close.getByLabel(t('pos.counted')).fill(String(200 + total - tea.price));
  await close.getByRole('button', { name: t('pos.closeShift') }).click();
  const z = page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: t('pos.zReport') }) });
  await expect(z).toBeVisible();
  await expect(z).toContainText((total - tea.price).toFixed(2));
});

test('without internet the sale is kept on the phone and sent when the connection is back', async ({ page, context }) => {
  const tea = shop().items[2];
  await startShift(page);

  await context.setOffline(true);
  await page.locator('.tile', { hasText: tea.nameAr }).click();
  await page.getByRole('button', { name: t('pos.exactCash') }).click();
  await page.getByRole('button', { name: t('pos.pay'), exact: true }).click();
  await expect(toast(page)).toContainText(t('pos.savedOffline'));
  await expect(page.getByText(`${t('pos.queued')}: 1`)).toBeVisible();
  await expect(page.locator('.last')).toContainText(t('pos.provisional'));

  await context.setOffline(false);
  await expect(page.getByText(`${t('pos.queued')}: 1`)).toBeHidden({ timeout: 20_000 });
});

test('a discount over the cashier limit goes through once the owner types the PIN on the phone', async ({ page }) => {
  const s = shop();
  const rice = s.items[1];
  await startShift(page);
  const api = await Api.signIn(new URL(page.url()).origin, s.code, s.owner.email, s.owner.password);
  await api.put('sales/pos/my-pin', { pin: '583920' });

  await page.locator('.tile', { hasText: rice.nameAr }).click();
  // 20% off is past the shop's 10% limit.
  page.once('dialog', (d) => d.accept('20'));
  await page.locator('.line').getByRole('button').last().click();
  await page.getByRole('menuitem', { name: t('pos.discount') }).click();
  await expect(page.locator('.total')).toContainText((rice.price * 0.8).toFixed(2));
  await page.getByRole('button', { name: t('pos.exactCash') }).click();
  await page.getByRole('button', { name: t('pos.pay'), exact: true }).click();

  const approval = page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: t('pos.approvalTitle') }) });
  await expect(approval).toBeVisible();
  await approval.getByLabel(t('pos.pin')).fill('111111');
  await approval.getByRole('button', { name: t('pos.approve') }).click();
  await expect(toast(page)).toContainText('الرمز السري غير صحيح'); // the server's Arabic message
  await approval.getByLabel(t('pos.pin')).fill('583920');
  await approval.getByRole('button', { name: t('pos.approve') }).click();
  await expect(approval).toBeHidden();
  await expect(page.locator('.last b.ltr')).toHaveText(/\S+/);
  await expect(page.getByText(t('pos.emptyCart'))).toBeVisible();
});
