import { expect, test } from '@playwright/test';
import { Api, shop } from './support/api';
import { signIn, t } from './support/app';

interface Master { id: string; code: string }

/** A shopper's order through the public storefront API, as the shop website or app will send it. */
async function shopperOrders(base: string, slug: string, sku: string, quantity: number): Promise<string> {
  const store = new Api(base);
  const { token } = await store.post<{ token: string }>(`store/${slug}/cart`, {});
  const headers = { 'X-Cart-Token': token };
  await store.put(`store/${slug}/cart/lines`, { productSlug: sku.toLowerCase(), quantity }, headers);
  const placed = await store.post<{ orderNo: string }>(`store/${slug}/checkout`, {
    customerName: 'Noora', phone: '+974 5566 7788', zone: '25', street: '900', building: '12', unit: '4', notes: 'Ring the bell'
  }, headers);
  return placed.orderNo;
}

test('an online cash-on-delivery order is delivered and its cash received from the online orders screen', async ({ page, baseURL }) => {
  const s = shop();
  const tea = s.items[2];
  const owner = await Api.signIn(baseURL!, s.code, s.owner.email, s.owner.password);

  // The owner opens the store: dispatch warehouse, a delivery item, tea in the store.
  const [warehouses, groups, uoms, items] = await Promise.all([
    owner.get<Master[]>('inventory/warehouses'), owner.get<Master[]>('inventory/item-groups'), owner.get<Master[]>('inventory/uoms'),
    owner.get<{ items: { id: string; sku: string }[] }>(`inventory/items?search=${tea.sku}`)
  ]);
  const delivery = await owner.post<{ id: string }>('inventory/items', {
    sku: `DLV-${Date.now().toString(36)}`, nameEn: 'Delivery', nameAr: 'توصيل', itemType: 'Service', itemGroupId: groups[0].id,
    uomId: (uoms.find((u) => u.code === 'PCS') ?? uoms[0]).id, salesPrice: 0, isActive: true
  });
  const slug = `${s.code}-store`;
  await owner.put('commerce/store', {
    slug, nameEn: 'E2E Grocery', nameAr: 'بقالة الاختبار', codEnabled: true, minOrder: 0, deliveryFee: 10, status: 'Open',
    warehouseId: (warehouses.find((w) => w.code === 'MAIN') ?? warehouses[0]).id, deliveryItemId: delivery.id
  });
  // Publishing is safe to repeat (the phone shop test lists tea too); a new listing takes the SKU as its address.
  await owner.post('commerce/listings/publish', { itemIds: [items.items.find((i) => i.sku === tea.sku)!.id], publish: true });
  const orderNo = await shopperOrders(baseURL!, slug, tea.sku, 2);

  await signIn(page);
  await page.goto('/store/orders');
  await expect(page.getByRole('heading', { name: t('store.orders') })).toBeVisible();
  const card = page.locator(`[data-order="${orderNo}"]`);
  await expect(card).toContainText('Noora');
  await expect(card).toContainText('Zone 25, Street 900, Building 12, Unit 4');
  await expect(card).toContainText(t('store.stageOf.New'));
  await expect(card).toContainText((tea.price * 2 + 10).toFixed(2));

  // Deliver: delivery note and invoice; then the driver's cash.
  await card.getByRole('button', { name: t('store.deliver') }).click();
  await expect(card).toContainText(t('store.stageOf.ToCollect'));
  await card.getByRole('button', { name: new RegExp(t('store.collect', { amount: '' }).trim()) }).click();
  await expect(card).toBeHidden();

  await page.getByRole('option', { name: t('store.stage.done') }).click();
  await expect(page.locator(`[data-order="${orderNo}"]`)).toContainText(t('store.stageOf.Done'));
});
