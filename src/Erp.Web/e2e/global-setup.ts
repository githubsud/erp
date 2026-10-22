import { writeFileSync } from 'node:fs';
import { FullConfig } from '@playwright/test';
import { Api, iban, Shop, SHOP_FILE, today } from './support/api';

interface Master { id: string; code: string }

/**
 * A new shop for every run, so the tests never depend on (or change) anyone's data: products with stock, a till, a cashier, and an
 * employee with a contract and WPS settings. Created with the platform key (E2E_PLATFORM_KEY, the development key by default).
 */
export default async function globalSetup(config: FullConfig): Promise<void> {
  const base = config.projects[0].use.baseURL!;
  const code = `e2e-${Date.now().toString(36)}`;
  const owner = { email: `owner@${code}.qa`, password: 'E2e-Owner-Password-1' };
  const cashier = { email: `cashier@${code}.qa`, password: 'E2e-Cashier-Password-1' };

  await new Api(base).post('platform/tenants', {
    code, nameEn: 'E2E Grocery', nameAr: 'بقالة الاختبار', establishmentId: '12345678', adminEmail: owner.email, adminPassword: owner.password,
    adminNameEn: 'Owner', adminNameAr: 'المالك', preferredLanguage: 'ar'
  }, { 'X-Platform-Key': process.env['E2E_PLATFORM_KEY'] ?? 'dev-platform-key' });
  const api = await Api.signIn(base, code, owner.email, owner.password);

  // Products with opening stock.
  const [groups, uoms, warehouses, accounts] = await Promise.all([
    api.get<Master[]>('inventory/item-groups'), api.get<Master[]>('inventory/uoms'), api.get<Master[]>('inventory/warehouses'),
    api.get<Master[]>('finance/accounts')
  ]);
  const warehouse = warehouses.find((w) => w.code === 'MAIN') ?? warehouses[0];
  const items = [
    { sku: 'MILK', nameEn: 'Fresh milk 1L', nameAr: 'حليب طازج 1 لتر', price: 6.5, cost: 5 },
    { sku: 'RICE', nameEn: 'Basmati rice 5kg', nameAr: 'أرز بسمتي 5 كغ', price: 32, cost: 25 },
    { sku: 'TEA', nameEn: 'Black tea 100 bags', nameAr: 'شاي أسود 100 كيس', price: 12, cost: 8 }
  ];
  const ids: string[] = [];
  for (const i of items) {
    const item = await api.post<{ id: string }>('inventory/items', {
      sku: i.sku, nameEn: i.nameEn, nameAr: i.nameAr, itemType: 'Stock', itemGroupId: groups[0].id, uomId: (uoms.find((u) => u.code === 'PCS') ?? uoms[0]).id,
      salesPrice: i.price, purchasePrice: i.cost, isActive: true
    });
    ids.push(item.id);
  }
  await api.post('inventory/adjustments', {
    warehouseId: warehouse.id, date: today(), reason: 'Opening', notes: 'E2E opening stock',
    lines: items.map((i, n) => ({ itemId: ids[n], quantity: 100, unitCost: i.cost }))
  });

  await api.post('sales/pos/registers', {
    code: 'T1', nameEn: 'Till 1', nameAr: 'الصندوق 1', warehouseId: warehouse.id,
    cashAccountId: accounts.find((a) => a.code === '1110')!.id, cardAccountId: accounts.find((a) => a.code === '1115')!.id
  });
  await api.post('core/users', {
    email: cashier.email, displayNameEn: 'Cashier', displayNameAr: 'الكاشير', password: cashier.password, preferredLanguage: 'ar', roles: ['salesperson']
  });

  // Payroll: WPS settings, one employee with a contract from the start of last month.
  const settings = await api.get<Record<string, unknown>>('hr/settings');
  await api.put('hr/settings', {
    ...settings, employerEid: '1234567', payerEid: '1234567', payerBankShortName: 'QNBA', payerIban: iban('QNBA', '000000000000693123456')
  });
  const now = new Date(today());
  const joined = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - 1, 1)).toISOString().slice(0, 10);
  const employee = { empNo: 'E001', nameEn: 'Ramesh Kumar' };
  const hired = await api.post<{ id: string }>('hr/employees', {
    empNo: employee.empNo, nameEn: employee.nameEn, nameAr: 'راميش كومار', nationality: 'IN', gender: 'Male', joiningDate: joined,
    qid: '28435612345', iban: iban('CBQA', '000000000001111111111'), socialInsuranceScheme: 'None'
  });
  await api.post(`hr/employees/${hired.id}/contracts`, {
    startDate: joined, contractType: 'Unlimited', probationMonths: 0, accommodationProvided: true, foodProvided: true, gratuityBasis: 'Basic',
    components: [{ componentCode: 'BASIC', amount: 3000 }, { componentCode: 'HOUSING', amount: 1000 }]
  });

  const result: Shop = { code, owner, cashier, items: items.map(({ sku, nameAr, price }) => ({ sku, nameAr, price })), employee };
  writeFileSync(SHOP_FILE, JSON.stringify(result, null, 2));
}
