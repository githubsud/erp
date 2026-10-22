import { expect, test } from '@playwright/test';
import { signIn, t, toast } from './support/app';

test('help articles are searched in Arabic, and a support request suggests them before it is sent', async ({ page }) => {
  await signIn(page);
  await page.goto('/help');
  await page.getByLabel(t('help.search')).fill('طابعة');
  const article = page.locator('a.article').first();
  await expect(article).toBeVisible();
  await article.click();
  await expect(page).toHaveURL(/\/help\/[\w-]+$/);
  await expect(page.locator('mat-card.body')).toContainText('طابعة');

  await page.goto('/support');
  await page.getByRole('button', { name: t('help.new') }).first().click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel(t('help.subject')).fill('الطابعة لا تطبع الإيصال');
  await expect(dialog.getByText(t('help.maybe'))).toBeVisible();
  await dialog.getByLabel(t('help.body')).fill('بعد البيع لا يخرج الإيصال من طابعة الكاشير.');
  await dialog.getByRole('button', { name: t('help.send') }).click();
  await expect(toast(page)).toContainText('SUP-');
  await expect(page.getByText('الطابعة لا تطبع الإيصال')).toBeVisible();
});
