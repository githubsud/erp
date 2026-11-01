import { expect, test } from '@playwright/test';
import { Api, shop, today } from './support/api';
import { signIn, t } from './support/app';

interface Master { id: string; code: string }

test('the owner receives a partner shop\'s goods on trust and prints the slip', async ({ page, baseURL }) => {
  const s = shop();
  const owner = await Api.signIn(baseURL!, s.code, s.owner.email, s.owner.password);
  const run = String(Date.now()).slice(-6);

  // A perfume shop (a supplier) with its own new item, so other tests' items stay ours.
  const supplier = await owner.post<{ id: string }>('purchasing/suppliers', {
    code: `OUD${run}`, nameEn: `Oud House ${run}`, nameAr: `بيت العود ${run}`, country: 'QA', isResident: true, hasPermanentEstablishment: false,
    paymentTermsDays: 30, defaultWhtCategory: 'None'
  });
  const [groups, uoms] = await Promise.all([owner.get<Master[]>('inventory/item-groups'), owner.get<Master[]>('inventory/uoms')]);
  const sku = `OUD-${run}`;
  const item = await owner.post<{ id: string }>('inventory/items', {
    sku, nameEn: `Oud oil ${run}`, nameAr: `دهن عود ${run}`, itemType: 'Stock', itemGroupId: groups[0].id,
    uomId: (uoms.find((u) => u.code === 'PCS') ?? uoms[0]).id, salesPrice: 250
  });
  await owner.put('purchasing/consignment/shops', {
    supplierId: supplier.id, commissionType: 'Percent', commission: 10, startsOn: today(), payoutDay: 5, isActive: true, applyToItems: true
  });
  await owner.post(`purchasing/consignment/shops/${supplier.id}/items`, { itemIds: [item.id] });

  await signIn(page);
  await page.goto('/purchasing/partner-shops');
  await page.getByRole('row', { name: new RegExp(`بيت العود ${run}`) }).click();
  await expect(page.getByRole('heading', { name: `بيت العود ${run}` })).toBeVisible();

  await page.getByRole('tab', { name: t('gt.goods') }).click();
  await page.getByRole('button', { name: t('gt.receive') }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel(`${t('gt.bottles')} ${sku}`).fill('3');
  await dialog.getByRole('button', { name: t('gt.saveBottles', { n: 3 }) }).click();

  // The slip to sign opens: three bottles, still the shop's property.
  await expect(page.getByRole('heading', { name: t('gt.slip.Receipt') })).toBeVisible();
  await expect(page.getByRole('cell', { name: sku })).toBeVisible();
  await expect(page.getByText(t('gt.slipNote'))).toBeVisible();
});
