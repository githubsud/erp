import { expect, test } from '@playwright/test';
import { Api, shop } from './support/api';
import { signIn, t } from './support/app';

interface Master { id: string; code: string }
interface Listing { id: string; itemId: string; images: string[] }

/** A 1×1 red PNG, as a phone would hand over a photo file. */
const PHOTO = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==', 'base64');

test('the owner adds a product photo and shoppers see it in the shop', async ({ page, browser, baseURL }) => {
  const s = shop();
  const rice = s.items[1];
  const owner = await Api.signIn(baseURL!, s.code, s.owner.email, s.owner.password);

  // An open store with rice in it and no photos yet (the test can run again on the same shop).
  const [warehouses, items] = await Promise.all([
    owner.get<Master[]>('inventory/warehouses'),
    owner.get<{ items: { id: string; sku: string }[] }>(`inventory/items?search=${rice.sku}`)
  ]);
  const riceId = items.items.find((i) => i.sku === rice.sku)!.id;
  const store = await owner.get<{ deliveryItemId: string | null }>('commerce/store');
  const slug = `${s.code}-store`;
  await owner.put('commerce/store', {
    slug, nameEn: 'E2E Grocery', nameAr: 'بقالة الاختبار', codEnabled: true, minOrder: 0, deliveryFee: 10, status: 'Open',
    warehouseId: (warehouses.find((w) => w.code === 'MAIN') ?? warehouses[0]).id, deliveryItemId: store.deliveryItemId
  });
  await owner.post('commerce/listings/publish', { itemIds: [riceId], publish: true });
  const listing = (await owner.get<Listing[]>('commerce/listings')).find((l) => l.itemId === riceId)!;
  for (const id of listing.images) await owner.delete(`commerce/listings/${listing.id}/images/${id}`);

  await signIn(page);
  await page.goto('/store/products');
  await page.getByLabel(t('app.search')).fill(rice.sku);
  await page.getByRole('button', { name: `${t('store.photos')} ${rice.sku}` }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText(t('store.noPhotos'))).toBeVisible();
  await dialog.getByTestId('photo-input').setInputFiles({ name: 'rice.png', mimeType: 'image/png', buffer: PHOTO });
  await expect(dialog.locator('figure img')).toHaveCount(1);
  await expect(dialog.getByText(t('store.firstPhoto'))).toBeVisible();
  await dialog.getByRole('button', { name: t('app.close') }).click();
  await expect(page.locator('app-auth-img.thumb img')).toHaveCount(1);

  // A shopper (no sign-in) sees the photo on the product card.
  const shopper = await browser.newPage();
  await shopper.addInitScript(() => localStorage.setItem('erp.lang', 'ar'));
  await shopper.goto(`/shop/${slug}`);
  const card = shopper.locator('mat-card', { hasText: rice.nameAr });
  await expect(card.locator('.pic img')).toBeVisible({ timeout: 30_000 });
  expect(await card.locator('.pic img').evaluate((img: HTMLImageElement) => img.naturalWidth)).toBeGreaterThan(0);
  await shopper.close();
});
