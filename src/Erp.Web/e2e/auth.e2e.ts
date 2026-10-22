import { expect, test } from '@playwright/test';
import { shop } from './support/api';
import { signIn, t } from './support/app';

test('a wrong password is refused with an Arabic message, the right one opens the first-day list', async ({ page }) => {
  const s = shop();
  await page.addInitScript(() => localStorage.setItem('erp.lang', 'ar'));
  await page.goto('/login');
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await page.locator('input[formcontrolname=tenantCode]').fill(s.code);
  await page.locator('input[formcontrolname=email]').fill(s.owner.email);
  await page.locator('input[formcontrolname=password]').fill('not-the-password');
  await page.getByRole('button', { name: t('login.submit') }).click();
  await expect(page.getByRole('alert')).toContainText('كلمة المرور غير صحيحة');

  await signIn(page);
  await expect(page.getByText(t('gs.title'))).toBeVisible();
});

test('a cashier cannot open payroll, employees or user management, even by typing the address', async ({ page }) => {
  await signIn(page, 'cashier');
  for (const path of ['/payroll', '/employees', '/settings/users']) {
    await page.goto(path);
    await expect(page).not.toHaveURL(new RegExp(path.replace('/', '\\/') + '$'));
  }
  // The till is theirs.
  await page.goto('/sales/pos');
  await expect(page.getByRole('heading', { name: t('pos.title') })).toBeVisible();
});
