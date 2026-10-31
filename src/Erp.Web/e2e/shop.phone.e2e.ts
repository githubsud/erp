import { expect, test } from '@playwright/test';
import { Api, shop } from './support/api';
import { t } from './support/app';

interface Master { id: string; code: string }

/** Opens the e2e shop's online store with rice and tea in it (the same settings each time, so tests can run in any order). */
async function openStore(base: string): Promise<string> {
  const s = shop();
  const owner = await Api.signIn(base, s.code, s.owner.email, s.owner.password);
  const [warehouses, groups, uoms] = await Promise.all([
    owner.get<Master[]>('inventory/warehouses'), owner.get<Master[]>('inventory/item-groups'), owner.get<Master[]>('inventory/uoms')
  ]);
  const services = await owner.get<{ items: { id: string; sku: string }[] }>('inventory/items?search=DLV-SHOP');
  const delivery = services.items.find((i) => i.sku === 'DLV-SHOP') ?? await owner.post<{ id: string }>('inventory/items', {
    sku: 'DLV-SHOP', nameEn: 'Delivery', nameAr: 'توصيل', itemType: 'Service', itemGroupId: groups[0].id,
    uomId: (uoms.find((u) => u.code === 'PCS') ?? uoms[0]).id, salesPrice: 0, isActive: true
  });
  const slug = `${s.code}-store`;
  await owner.put('commerce/store', {
    slug, nameEn: 'E2E Grocery', nameAr: 'بقالة الاختبار', codEnabled: true, minOrder: 0, deliveryFee: 10, status: 'Open',
    warehouseId: (warehouses.find((w) => w.code === 'MAIN') ?? warehouses[0]).id, deliveryItemId: delivery.id
  });
  const items = await owner.get<{ items: { id: string; sku: string }[] }>('inventory/items?pageSize=50');
  await owner.post('commerce/listings/publish', { itemIds: items.items.filter((i) => ['RICE', 'TEA'].includes(i.sku)).map((i) => i.id), publish: true });
  return slug;
}

test('a shopper on a phone finds a product, orders it cash on delivery and sees the order', async ({ page, baseURL }) => {
  const [, rice, tea] = shop().items;
  const slug = await openStore(baseURL!);
  await page.addInitScript(() => localStorage.setItem('erp.lang', 'ar'));

  // No sign-in: the shop is open to anyone with its address.
  await page.goto(`/shop/${slug}`);
  await expect(page.getByRole('link', { name: 'بقالة الاختبار' })).toBeVisible({ timeout: 30_000 });
  await page.getByPlaceholder(t('shop.searchHint')).fill('شاي');
  const card = page.locator('mat-card', { hasText: tea.nameAr });
  await expect(card).toBeVisible();
  await expect(page.locator('mat-card', { hasText: rice.nameAr })).toBeHidden();
  // Nothing to scroll sideways on the phone.
  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(1);

  // The product page: two of them into the cart.
  await card.getByRole('link').last().click();
  await page.getByRole('button', { name: t('shop.moreQty') }).click();
  await page.getByRole('button', { name: t('shop.add') }).click();
  await expect(page.getByRole('heading', { name: t('shop.cart') })).toBeVisible();
  await expect(page.locator('.sum .total')).toContainText((tea.price * 2 + 10).toFixed(2));

  await page.getByRole('button', { name: t('shop.checkout') }).click();
  await page.getByLabel(t('shop.name')).fill('مريم');
  await page.getByLabel(t('shop.phone')).fill('+974 5577 8899');
  await page.getByLabel(t('shop.zone')).fill('56');
  await page.getByLabel(t('shop.street')).fill('340');
  await page.getByLabel(t('shop.building')).fill('7');
  await page.getByRole('button', { name: t('shop.place') }).click();

  await expect(page.getByRole('heading', { name: t('shop.status.Received') })).toBeVisible();
  const orderNo = (await page.locator('.done b.ltr').textContent())!.trim();
  expect(orderNo).toMatch(/^WEB/);
  await expect(page.locator('.lines .total')).toContainText((tea.price * 2 + 10).toFixed(2));

  // The order is in the shop's queue.
  const s = shop();
  const owner = await Api.signIn(baseURL!, s.code, s.owner.email, s.owner.password);
  const queue = await owner.get<{ orderNo: string; customerName: string }[]>('commerce/orders');
  expect(queue.find((o) => o.orderNo === orderNo)?.customerName).toBe('مريم');
});

test('a store that is not open is not there', async ({ page }) => {
  await page.goto('/shop/no-such-store-here');
  await expect(page.getByRole('heading', { name: t('shop.missing') })).toBeVisible();
});
