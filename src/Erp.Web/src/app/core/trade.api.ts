import { Injectable, inject } from '@angular/core';
import { ApiService } from './api.service';
import { Paged } from './models';
import {
  Adjustment, AgingReport, ItemUnit, GoodsReceipt, GrniReport, Item, ItemLedger, LandedCost, Master, PurchaseOrder, PurchaseSettings, StockCount, StockRow,
  Supplier, SupplierInvoice, SupplierPayment, Transfer, ValuationReport, WhtReport, DebitNote
} from './trade.models';

/** Inventory and purchasing endpoints. */
/** An alternative unit of an item (a box of 12), with its own price and barcode when it has them. */
export interface UnitRequest {
  uomId: string;
  factor: number;
  salesPrice?: number | null;
  barcode?: string | null;
}

@Injectable({ providedIn: 'root' })
export class TradeApi {
  private readonly api = inject(ApiService);

  // Inventory masters
  inventorySettings = () => this.api.get<{ allowNegativeStock: boolean }>('inventory/settings');
  saveInventorySettings = (allowNegativeStock: boolean) => this.api.put<{ allowNegativeStock: boolean }>('inventory/settings', { allowNegativeStock });
  uoms = () => this.api.get<Master[]>('inventory/uoms');
  createUom = (body: unknown) => this.api.post<Master>('inventory/uoms', body);
  groups = () => this.api.get<Master[]>('inventory/item-groups');
  saveGroup = (id: string | null, body: unknown) =>
    id ? this.api.put<Master>(`inventory/item-groups/${id}`, body) : this.api.post<Master>('inventory/item-groups', body);
  warehouses = () => this.api.get<Master[]>('inventory/warehouses');
  saveWarehouse = (id: string | null, body: unknown) =>
    id ? this.api.put<Master>(`inventory/warehouses/${id}`, body) : this.api.post<Master>('inventory/warehouses', body);
  items = (params: { search?: string; type?: string; active?: boolean; page?: number; pageSize?: number } = {}) =>
    this.api.get<Paged<Item>>('inventory/items', params);
  item = (id: string) => this.api.get<Item>(`inventory/items/${id}`);
  saveItem = (id: string | null, body: unknown) => (id ? this.api.put<Item>(`inventory/items/${id}`, body) : this.api.post<Item>('inventory/items', body));
  units = (id: string) => this.api.get<ItemUnit[]>(`inventory/items/${id}/units`);
  saveUnits = (id: string, units: UnitRequest[]) => this.api.put<ItemUnit[]>(`inventory/items/${id}/units`, units);
  itemLedger = (id: string, from: string, to: string) => this.api.get<ItemLedger>(`inventory/items/${id}/ledger`, { from, to });

  // Stock
  stock = (warehouseId?: string | null, belowReorder = false) => this.api.get<StockRow[]>('inventory/stock', { warehouseId, belowReorder });
  valuation = (asOf: string) => this.api.get<ValuationReport>('inventory/reports/valuation', { asOf });
  adjustments = () => this.api.get<Adjustment[]>('inventory/adjustments');
  createAdjustment = (body: unknown) => this.api.post<Adjustment>('inventory/adjustments', body);
  reverseAdjustment = (id: string, date: string, reason: string) => this.api.post<Adjustment>(`inventory/adjustments/${id}/reverse`, { date, reason });
  transfers = () => this.api.get<Transfer[]>('inventory/transfers');
  createTransfer = (body: unknown) => this.api.post<Transfer>('inventory/transfers', body);
  reverseTransfer = (id: string, date: string, reason: string) => this.api.post<Transfer>(`inventory/transfers/${id}/reverse`, { date, reason });
  counts = () => this.api.get<StockCount[]>('inventory/counts');
  count = (id: string) => this.api.get<StockCount>(`inventory/counts/${id}`);
  openCount = (body: unknown) => this.api.post<StockCount>('inventory/counts', body);
  enterCounts = (id: string, lines: { itemId: string; countedQuantity: number | null }[]) => this.api.put<StockCount>(`inventory/counts/${id}/lines`, lines);
  countAction = (id: string, action: 'post' | 'cancel') => this.api.post<StockCount>(`inventory/counts/${id}/${action}`);

  // Purchasing
  purchaseSettings = () => this.api.get<PurchaseSettings>('purchasing/settings');
  savePurchaseSettings = (body: PurchaseSettings) => this.api.put<PurchaseSettings>('purchasing/settings', body);
  suppliers = (search?: string) => this.api.get<Supplier[]>('purchasing/suppliers', { search });
  saveSupplier = (id: string | null, body: unknown) =>
    id ? this.api.put<Supplier>(`purchasing/suppliers/${id}`, body) : this.api.post<Supplier>('purchasing/suppliers', body);
  supplierBalance = (id: string) => this.api.get<{ supplierId: string; balance: number }>(`purchasing/suppliers/${id}/balance`);
  orders = (status?: string, supplierId?: string) => this.api.get<PurchaseOrder[]>('purchasing/orders', { status, supplierId });
  order = (id: string) => this.api.get<PurchaseOrder>(`purchasing/orders/${id}`);
  saveOrder = (id: string | null, body: unknown) =>
    id ? this.api.put<PurchaseOrder>(`purchasing/orders/${id}`, body) : this.api.post<PurchaseOrder>('purchasing/orders', body);
  orderAction = (id: string, action: 'submit' | 'approve' | 'reject' | 'cancel' | 'close', comment?: string | null) =>
    this.api.post<PurchaseOrder>(`purchasing/orders/${id}/${action}`, { comment: comment ?? null });
  setGtaNotification = (id: string, reference: string) => this.api.put<PurchaseOrder>(`purchasing/orders/${id}/gta-notification`, { reference });
  receipts = (params: { purchaseOrderId?: string; supplierId?: string } = {}) => this.api.get<GoodsReceipt[]>('purchasing/receipts', params);
  createReceipt = (body: unknown) => this.api.post<GoodsReceipt>('purchasing/receipts', body);
  reverseReceipt = (id: string, date: string, reason: string) => this.api.post<GoodsReceipt>(`purchasing/receipts/${id}/reverse`, { date, reason });
  invoices = (params: { supplierId?: string; status?: string; openOnly?: boolean } = {}) => this.api.get<SupplierInvoice[]>('purchasing/invoices', params);
  invoice = (id: string) => this.api.get<SupplierInvoice>(`purchasing/invoices/${id}`);
  createInvoice = (body: unknown) => this.api.post<SupplierInvoice>('purchasing/invoices', body);
  deleteInvoice = (id: string) => this.api.delete(`purchasing/invoices/${id}`);
  invoiceAction = (id: string, action: 'post' | 'approve-variance') => this.api.post<SupplierInvoice>(`purchasing/invoices/${id}/${action}`);
  reverseInvoice = (id: string, date: string, reason: string) => this.api.post<SupplierInvoice>(`purchasing/invoices/${id}/reverse`, { date, reason });
  debitNotes = (params: { supplierId?: string; supplierInvoiceId?: string } = {}) => this.api.get<DebitNote[]>('purchasing/debit-notes', params);
  createDebitNote = (body: unknown) => this.api.post<DebitNote>('purchasing/debit-notes', body);
  reverseDebitNote = (id: string, date: string, reason: string) => this.api.post<DebitNote>(`purchasing/debit-notes/${id}/reverse`, { date, reason });
  landedCosts = () => this.api.get<LandedCost[]>('purchasing/landed-costs');
  createLandedCost = (body: unknown) => this.api.post<LandedCost>('purchasing/landed-costs', body);
  reverseLandedCost = (id: string, date: string, reason: string) => this.api.post<LandedCost>(`purchasing/landed-costs/${id}/reverse`, { date, reason });
  payments = (supplierId?: string) => this.api.get<SupplierPayment[]>('purchasing/payments', { supplierId });
  createPayment = (body: unknown) => this.api.post<SupplierPayment>('purchasing/payments', body);
  clearPayment = (id: string, date: string) => this.api.post<SupplierPayment>(`purchasing/payments/${id}/clear`, { date });
  reversePayment = (id: string, date: string, reason: string) => this.api.post<SupplierPayment>(`purchasing/payments/${id}/reverse`, { date, reason });
  grni = () => this.api.get<GrniReport>('purchasing/reports/grni');
  aging = (asOf: string) => this.api.get<AgingReport>('purchasing/reports/aging', { asOf });
  wht = (year: number, month: number) => this.api.get<WhtReport>('purchasing/reports/wht', { year, month });
}
