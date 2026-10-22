import { expect, Page } from '@playwright/test';
import { ar } from '../../src/app/core/i18n/ar';
import { TranslationKey } from '../../src/app/core/i18n/en';
import { shop } from './api';

/** The Arabic text the app shows for a key, so tests follow wording changes instead of breaking on them. */
export function t(key: TranslationKey, params: Record<string, string | number> = {}): string {
  return ar[key].replace(/\{(\w+)\}/g, (all, name: string) => (name in params ? String(params[name]) : all));
}

/** Signs in through the login page, in Arabic, and waits for the app to open. */
export async function signIn(page: Page, who: 'owner' | 'cashier' = 'owner'): Promise<void> {
  const s = shop();
  await page.addInitScript(() => localStorage.setItem('erp.lang', 'ar'));
  await page.goto('/login');
  await page.locator('input[formcontrolname=tenantCode]').fill(s.code);
  await page.locator('input[formcontrolname=email]').fill(s[who].email);
  await page.locator('input[formcontrolname=password]').fill(s[who].password);
  await page.getByRole('button', { name: t('login.submit') }).click();
  await expect(page).not.toHaveURL(/\/login/);
}

/** The pop-up message at the bottom of the screen. */
export function toast(page: Page) {
  return page.locator('.mat-mdc-snack-bar-container, mat-snack-bar-container').last();
}
