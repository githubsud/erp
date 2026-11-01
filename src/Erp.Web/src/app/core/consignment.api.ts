import { inject, Injectable } from '@angular/core';
import { ApiService } from './api.service';

export type CommissionType = 'Percent' | 'Fixed';

/** A partner shop: a supplier whose goods we sell as its agent (goods on trust). Owed: its share of sales not settled yet (QAR). */
export interface PartnerShop {
  id: string;
  supplierId: string;
  supplierCode: string;
  nameEn: string;
  nameAr: string;
  phone: string | null;
  crNo: string | null;
  commissionType: CommissionType;
  commission: number;
  startsOn: string;
  endsOn: string | null;
  payoutDay: number;
  notes: string | null;
  isActive: boolean;
  itemCount: number;
  onHand: number;
  owed: number;
  settledTo: string | null;
}

export interface ShopRequest {
  supplierId: string;
  commissionType: CommissionType;
  commission: number;
  startsOn: string;
  endsOn: string | null;
  payoutDay: number;
  notes: string | null;
  isActive: boolean;
  applyToItems: boolean;
}

export interface ConsignedItem {
  itemId: string;
  sku: string;
  nameEn: string;
  nameAr: string;
  commissionType: CommissionType;
  commission: number;
  salesPrice: number | null;
  onHand: number;
}

export type MovementType = 'Receipt' | 'Return';

export interface MovementLine {
  itemId: string;
  sku: string;
  nameEn: string;
  nameAr: string;
  quantity: number;
}

export interface Movement {
  id: string;
  movementNo: string;
  movementType: MovementType;
  supplierId: string;
  supplierNameEn: string;
  supplierNameAr: string;
  warehouseId: string;
  date: string;
  notes: string | null;
  status: 'Posted' | 'Reversed';
  lines: MovementLine[];
}

export interface StatementLine {
  itemId: string;
  sku: string;
  nameEn: string;
  nameAr: string;
  sold: number;
  returned: number;
  sales: number;
  commission: number;
  due: number;
}

/** Adjustments: owed from before the period, so that amount is exactly what the shop is owed up to the period end. */
export interface Statement {
  supplierId: string;
  supplierCode: string;
  supplierNameEn: string;
  supplierNameAr: string;
  periodStart: string;
  periodEnd: string;
  lines: StatementLine[];
  sales: number;
  commission: number;
  due: number;
  adjustments: number;
  amount: number;
  onHand: ConsignedItem[];
  commissionType: CommissionType;
  commissionRate: number;
  settlementId: string | null;
  settlementNo: string | null;
  supplierInvoiceId: string | null;
  invoiceNo: string | null;
  invoiceStatus: string | null;
  paid: number | null;
}

export interface Settlement {
  id: string;
  settlementNo: string;
  supplierId: string;
  supplierNameEn: string;
  supplierNameAr: string;
  periodStart: string;
  periodEnd: string;
  sales: number;
  commission: number;
  amount: number;
  supplierInvoiceId: string;
  invoiceNo: string;
  invoiceStatus: string;
  paid: number;
}

export interface CommissionRow {
  month: string;
  supplierId: string;
  supplierNameEn: string;
  supplierNameAr: string;
  sold: number;
  sales: number;
  commission: number;
  due: number;
}

/** Goods on trust (Purchasing): partner shops, bottles in and out, statements and settlements. */
@Injectable({ providedIn: 'root' })
export class ConsignmentApi {
  private readonly api = inject(ApiService);
  private readonly base = 'purchasing/consignment';

  shops = () => this.api.get<PartnerShop[]>(`${this.base}/shops`);
  shop = (supplierId: string) => this.api.get<PartnerShop>(`${this.base}/shops/${supplierId}`);
  saveShop = (body: ShopRequest) => this.api.put<PartnerShop>(`${this.base}/shops`, body);
  items = (supplierId: string) => this.api.get<ConsignedItem[]>(`${this.base}/shops/${supplierId}/items`);
  addItems = (supplierId: string, itemIds: string[], commissionType: CommissionType | null, commission: number | null) =>
    this.api.post<ConsignedItem[]>(`${this.base}/shops/${supplierId}/items`, { itemIds, commissionType, commission });
  releaseItems = (supplierId: string, itemIds: string[]) => this.api.post<ConsignedItem[]>(`${this.base}/shops/${supplierId}/items/release`, { itemIds });
  movements = (supplierId?: string) => this.api.get<Movement[]>(`${this.base}/movements`, { supplierId });
  movement = (id: string) => this.api.get<Movement>(`${this.base}/movements/${id}`);
  move = (type: MovementType, body: unknown) => this.api.post<Movement>(`${this.base}/${type === 'Receipt' ? 'receipts' : 'returns'}`, body);
  reverseMovement = (id: string, date: string, reason: string) => this.api.post<Movement>(`${this.base}/movements/${id}/reverse`, { date, reason });
  preview = (supplierId: string, periodEnd: string) => this.api.get<Statement>(`${this.base}/statement`, { supplierId, periodEnd });
  settlements = (supplierId?: string) => this.api.get<Settlement[]>(`${this.base}/settlements`, { supplierId });
  settlement = (id: string) => this.api.get<Statement>(`${this.base}/settlements/${id}`);
  settle = (supplierId: string, periodEnd: string) => this.api.post<Statement>(`${this.base}/settlements`, { supplierId, periodEnd });
  commissions = (periodStart: string, periodEnd: string) => this.api.get<CommissionRow[]>(`${this.base}/commissions`, { periodStart, periodEnd });
}
