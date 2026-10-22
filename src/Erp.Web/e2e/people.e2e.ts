import { expect, test } from '@playwright/test';
import { iban, today } from './support/api';
import { signIn, t } from './support/app';

test('a new employee is added from the Arabic form, with QID and IBAN shown in full to the owner', async ({ page }) => {
  await signIn(page);
  await page.goto('/employees/new');

  await page.getByLabel(t('employee.nameAr')).fill('محمد عبد الله');
  await page.getByLabel(t('employee.nameEn')).fill('Mohamed Abdullah');
  await page.locator('mat-select[formcontrolname=nationality]').click();
  await page.getByRole('option', { name: 'مصر' }).click();
  await page.locator('mat-select[formcontrolname=gender]').click();
  await page.getByRole('option', { name: t('employee.male') }).click();
  await page.getByLabel(t('employee.empNo')).fill('E050');
  await page.getByLabel(t('employee.joiningDate')).fill(today());
  await page.getByLabel(t('employee.qid')).fill('28481812345');
  const account = iban('QNBA', '000000000005050505050');
  await page.getByLabel(t('employee.iban')).fill(account);
  await page.getByRole('button', { name: t('app.save') }).click();

  await expect(page).toHaveURL(/\/employees\/[0-9a-f-]{36}$/);
  await expect(page.getByRole('heading', { name: 'محمد عبد الله' })).toBeVisible();
  await expect(page.getByText('28481812345')).toBeVisible();
});
