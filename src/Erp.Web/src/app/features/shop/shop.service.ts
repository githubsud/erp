import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type Availability = 'InStock' | 'Low' | 'OutOfStock';

export interface ShopStore {
  slug: string;
  nameEn: string;
  nameAr: string;
  phone: string | null;
  currency: string;
  codEnabled: boolean;
  minOrder: number;
  deliveryFee: number;
}

export interface ShopCategory {
  slug: string;
  nameEn: string;
  nameAr: string;
  parentSlug: string | null;
  sortOrder: number;
}

export interface ShopUnit {
  uomCode: string;
  factor: number;
  price: number;
}

export interface ShopFitment {
  make: string;
  model: string;
  yearFrom: number | null;
  yearTo: number | null;
  engine: string | null;
}

export interface ShopProduct {
  slug: string;
  titleEn: string;
  titleAr: string;
  categorySlug: string | null;
  price: number;
  uomCode: string;
  units: ShopUnit[];
  availability: Availability;
  images: string[];
  brand: string | null;
  fits: ShopFitment[];
  descriptionEn: string | null;
  descriptionAr: string | null;
}

export interface ShopPage<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

export interface ShopCartLine {
  productSlug: string;
  titleEn: string;
  titleAr: string;
  uomCode: string;
  quantity: number;
  unitPrice: number;
  amount: number;
  availability: Availability;
  enough: boolean;
  available: boolean;
}

export interface ShopCart {
  lines: ShopCartLine[];
  subtotal: number;
  deliveryFee: number;
  total: number;
  minOrder: number;
  meetsMinimum: boolean;
  canCheckout: boolean;
}

export interface ShopCheckout {
  customerName: string;
  phone: string;
  zone: string;
  street: string;
  building: string;
  unit: string | null;
  notes: string | null;
  latitude: number | null;
  longitude: number | null;
}

export interface ShopOrderLine {
  titleEn: string;
  titleAr: string;
  uomCode: string;
  quantity: number;
  unitPrice: number;
  amount: number;
}

export interface ShopOrder {
  orderNo: string;
  status: 'Received' | 'Delivered' | 'Cancelled';
  placedAt: string;
  lines: ShopOrderLine[];
  subtotal: number;
  deliveryFee: number;
  total: number;
  paymentMethod: string;
}

/**
 * The shopper's side of one online store, over the public /api/store/{slug} endpoints. The cart key and the keys of placed
 * orders stay in this browser (per store); without them the server shows nothing.
 */
@Injectable({ providedIn: 'root' })
export class ShopService {
  private readonly http = inject(HttpClient);
  readonly slug = signal('');
  readonly store = signal<ShopStore | null>(null);
  readonly missing = signal(false);
  readonly cart = signal<ShopCart | null>(null);
  readonly count = computed(() => this.cart()?.lines.filter((l) => l.available).reduce((n, l) => n + l.quantity, 0) ?? 0);

  private get base(): string {
    return `/api/store/${encodeURIComponent(this.slug())}`;
  }

  private read(key: string): string | null {
    try {
      return localStorage.getItem(key);
    } catch {
      return null;
    }
  }

  private write(key: string, value: string | null): void {
    try {
      if (value === null) localStorage.removeItem(key);
      else localStorage.setItem(key, value);
    } catch {
      // Private mode: the cart lasts as long as the page.
    }
  }

  private get cartKey(): string {
    return `shop.cart.${this.slug()}`;
  }

  private headers(): HttpHeaders {
    const token = this.read(this.cartKey);
    return token ? new HttpHeaders({ 'X-Cart-Token': token }) : new HttpHeaders();
  }

  /** Opens a store: its profile, and the cart this browser already has there (if any). */
  async open(slug: string): Promise<void> {
    if (this.slug() === slug && this.store()) return;
    this.slug.set(slug);
    this.store.set(null);
    this.cart.set(null);
    this.missing.set(false);
    try {
      this.store.set(await firstValueFrom(this.http.get<ShopStore>(this.base)));
    } catch (e) {
      if (e instanceof HttpErrorResponse && e.status === 404) this.missing.set(true);
      else throw e;
      return;
    }
    if (this.read(this.cartKey)) await this.loadCart();
  }

  categories = () => this.http.get<ShopCategory[]>(`${this.base}/categories`);
  catalog = (params: { search?: string; category?: string; page?: number; pageSize?: number }) =>
    this.http.get<ShopPage<ShopProduct>>(`${this.base}/catalog`, {
      params: Object.fromEntries(Object.entries(params).filter(([, v]) => v !== undefined && v !== '')) as Record<string, string | number>
    });
  product = (slug: string) => this.http.get<ShopProduct>(`${this.base}/products/${encodeURIComponent(slug)}`);

  async loadCart(): Promise<void> {
    try {
      this.cart.set(await firstValueFrom(this.http.get<ShopCart>(`${this.base}/cart`, { headers: this.headers() })));
    } catch (e) {
      // A used or expired cart: start afresh next time something is added.
      if (e instanceof HttpErrorResponse && e.status === 404) {
        this.write(this.cartKey, null);
        this.cart.set(null);
      } else throw e;
    }
  }

  /** Sets the quantity of a product in a unit (0 removes it), creating the cart on the first product. */
  async setQuantity(productSlug: string, uomCode: string, quantity: number): Promise<void> {
    if (!this.read(this.cartKey)) {
      const created = await firstValueFrom(this.http.post<{ token: string; cart: ShopCart }>(`${this.base}/cart`, {}));
      this.write(this.cartKey, created.token);
    }
    try {
      this.cart.set(await firstValueFrom(this.http.put<ShopCart>(`${this.base}/cart/lines`, { productSlug, uomCode, quantity }, { headers: this.headers() })));
    } catch (e) {
      if (e instanceof HttpErrorResponse && e.status === 404 && e.error?.title?.includes?.('Cart')) {
        this.write(this.cartKey, null);
        return this.setQuantity(productSlug, uomCode, quantity);
      }
      throw e;
    }
  }

  /** Adds to what the cart already holds of this product and unit. */
  add(productSlug: string, uomCode: string, quantity = 1): Promise<void> {
    const line = this.cart()?.lines.find((l) => l.productSlug === productSlug && l.uomCode === uomCode);
    return this.setQuantity(productSlug, uomCode, (line?.quantity ?? 0) + quantity);
  }

  /** Places the cart as a cash-on-delivery order; the cart is used up and the order's key is kept in this browser. */
  async checkout(body: ShopCheckout): Promise<string> {
    const placed = await firstValueFrom(this.http.post<{ orderNo: string; statusToken: string }>(`${this.base}/checkout`, body, { headers: this.headers() }));
    this.write(`shop.order.${this.slug()}.${placed.orderNo}`, placed.statusToken);
    this.write(this.cartKey, null);
    this.cart.set(null);
    return placed.orderNo;
  }

  order(orderNo: string) {
    const token = this.read(`shop.order.${this.slug()}.${orderNo}`) ?? '';
    return this.http.get<ShopOrder>(`${this.base}/orders/${encodeURIComponent(orderNo)}`, { headers: new HttpHeaders({ 'X-Order-Token': token }) });
  }
}
