import { inject, Injectable } from '@angular/core';
import { ApiService } from './api.service';

export type StoreStatus = 'Draft' | 'Open' | 'Closed';

export interface Store {
  id: string;
  slug: string;
  nameEn: string;
  nameAr: string;
  phone: string | null;
  host: string | null;
  currency: string;
  codEnabled: boolean;
  minOrder: number;
  deliveryFee: number;
  status: StoreStatus;
  warehouseId: string | null;
  deliveryItemId: string | null;
}

export interface StoreListing {
  id: string;
  itemId: string;
  sku: string;
  slug: string;
  titleEn: string;
  titleAr: string;
  descriptionEn: string | null;
  descriptionAr: string | null;
  categoryId: string | null;
  isPublished: boolean;
  sortOrder: number;
}

export interface OnlineOrderLine {
  titleEn: string;
  titleAr: string;
  uomCode: string;
  quantity: number;
  unitPrice: number;
  amount: number;
}

/** New: to deliver. ToCollect: delivered and invoiced, cash still to receive. */
export type OnlineOrderStage = 'New' | 'ToCollect' | 'Done' | 'Cancelled';

export interface OnlineOrder {
  id: string;
  orderNo: string;
  placedAt: string;
  customerName: string;
  phone: string;
  address: string;
  addressNotes: string | null;
  latitude: number | null;
  longitude: number | null;
  subtotal: number;
  deliveryFee: number;
  total: number;
  paymentMethod: string;
  salesOrderId: string;
  soNo: string;
  customerId: string;
  stage: OnlineOrderStage;
  fullyDelivered: boolean;
  fullyInvoiced: boolean;
  outstanding: number;
  lines: OnlineOrderLine[];
}

/** The online store's merchant side (docs/Commerce-Storefront-Design.md). */
@Injectable({ providedIn: 'root' })
export class CommerceApi {
  private readonly api = inject(ApiService);

  store = () => this.api.get<Store>('commerce/store');
  saveStore = (body: unknown) => this.api.put<Store>('commerce/store', body);
  listings = () => this.api.get<StoreListing[]>('commerce/listings');
  publish = (itemIds: string[], publish: boolean) => this.api.post<{ count: number }>('commerce/listings/publish', { itemIds, publish });
  orders = (stage: 'open' | 'done' | 'all') => this.api.get<OnlineOrder[]>('commerce/orders', { stage });
}
