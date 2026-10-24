import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { EMPTY, filter, forkJoin, map, Observable, of, switchMap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { Account } from '../../core/models';
import { NotifyService, problemOf } from '../../core/notify.service';
import { ReceiptService } from '../../core/receipt.service';
import { LowItem, LowStockNotice, stockLevel } from '../../core/stock-alert';
import { inBoxes } from '../../core/item-units';
import { TradeApi } from '../../core/trade.api';
import { Master } from '../../core/trade.models';
import { PrinterSettingsDialog } from '../../shared/printer-settings';
import { SHARED } from '../../shared/shared';

interface Register {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  warehouseId: string;
  cashAccountId: string;
  cardAccountId: string;
  isActive: boolean;
  openShiftId: string | null;
}

interface Shift {
  id: string;
  shiftNo: string;
  registerId: string;
  registerCode: string;
  openedAt: string;
  openingFloat: number;
  status: 'Open' | 'Closed';
  closedAt: string | null;
  businessDate: string;
  expectedCash: number | null;
  countedCash: number | null;
  difference: number | null;
}

interface CatalogItem {
  id: string;
  sku: string;
  barcode: string | null;
  nameEn: string;
  nameAr: string;
  uomCode: string;
  type: string;
  salesPrice: number;
  /** Stock items only: company-wide quantity on hand (kept up to date on this device after each sale) and the reorder level. */
  onHand?: number | null;
  reorderLevel?: number | null;
  /** Boxes (or other units) it is also sold in: pieces per one, the box's price and barcode. */
  units?: CatalogUnit[] | null;
}

interface CatalogUnit {
  uomId: string;
  uomCode: string;
  factor: number;
  salesPrice: number;
  barcode: string | null;
}

interface CartLine {
  item: CatalogItem;
  /** Sold by the box; none: by the piece. Quantity and price are per this unit. */
  unit?: CatalogUnit | null;
  quantity: number;
  discountPercent: number;
  /** A price other than the item list's (a lower one counts against the cashier discount limit). */
  price?: number;
}

interface SaleLine {
  id: string;
  lineNo: number;
  itemId: string;
  sku: string;
  nameEn: string;
  nameAr: string;
  quantity: number;
  unitPrice: number;
  discountPercent: number;
  amount: number;
  refundable: number;
  /** Rung up by the box: the box and how many at what price (quantity and unit price above are in pieces). */
  uomCode?: string | null;
  uomQuantity?: number | null;
  uomPrice?: number | null;
}

interface Sale {
  id: string;
  receiptNo: string;
  soldAt: string;
  customerName: string | null;
  total: number;
  discountTotal: number;
  cashAmount: number;
  cardAmount: number;
  changeGiven: number;
  isRefund: boolean;
  lines: SaleLine[];
  /** Rung up without a connection: the receipt number comes when it reaches the server. */
  offline?: boolean;
}

interface ZReport {
  shift: Shift;
  salesCount: number;
  refundCount: number;
  grossSales: number;
  discounts: number;
  refunds: number;
  netSales: number;
  cashSales: number;
  cardSales: number;
  openingFloat: number;
  expectedCash: number;
  countedCash: number | null;
  difference: number | null;
  cost: number;
  topItems: { itemId: string; sku: string; nameEn: string; nameAr: string; quantity: number; amount: number }[];
}

/** A sale waiting to reach the server. The client id makes resending safe. */
interface QueuedSale {
  clientId: string;
  shiftId: string;
  soldAt: string;
  lines: { itemId: string; quantity: number; unitPrice: number; discountPercent: number; uomId?: string | null }[];
  cash: number;
  card: number;
  customerName: string | null;
  customerPhone: string | null;
  total: number;
  approvalId?: string | null;
  error?: string;
  errorCode?: string;
}

interface Policy {
  maxDiscountPercent: number;
  canOverride: boolean;
}

const CATALOG_KEY = 'erp.pos.catalog';
const QUEUE_KEY = 'erp.pos.queue';
const REGISTERS_KEY = 'erp.pos.registers';
const SHIFT_KEY = 'erp.pos.shift';
const POLICY_KEY = 'erp.pos.policy';

function readStore<T>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(key);
    return raw ? (JSON.parse(raw) as T) : fallback;
  } catch {
    return fallback;
  }
}

function writeStore(key: string, value: unknown): void {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // Storage full or blocked: the till still works online.
  }
}

const round2 = (v: number) => Math.round((v + Number.EPSILON) * 100) / 100;
/** The item list's price of one piece, or of one box (the box's own price). */
const listPriceOf = (l: CartLine) => l.unit?.salesPrice ?? l.item.salesPrice;
const priceOf = (l: CartLine) => l.price ?? listPriceOf(l);
const piecesOf = (l: CartLine) => l.quantity * (l.unit?.factor ?? 1);
const sameLine = (l: CartLine, item: CatalogItem, unit: CatalogUnit | null) => l.item.id === item.id && (l.unit?.uomId ?? null) === (unit?.uomId ?? null);
const lineAmount = (l: CartLine) => round2(l.quantity * priceOf(l) * (1 - l.discountPercent / 100));

/** Same rule as the server: how much comes off the item list's prices, and whether a line goes past the cashier limit. */
function measure(lines: { listPrice: number; quantity: number; amount: number }[], maxPercent: number): { cut: number; over: boolean } {
  let cut = 0;
  let over = false;
  for (const l of lines) {
    const full = round2(l.quantity * l.listPrice);
    if (l.amount < round2((full * (100 - maxPercent)) / 100)) over = true;
    cut += Math.max(0, full - l.amount);
  }
  return { cut: round2(cut), over };
}
const isOffline = (e: unknown) => e instanceof HttpErrorResponse && (e.status === 0 || e.status >= 502);

@Component({
  selector: 'app-pos',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'pos.title' | t }}</h1>
      <span class="spacer"></span>
      @if (queue().length) {
        <span class="chip warn" [matTooltip]="'pos.queuedHint' | t">{{ 'pos.queued' | t }}: {{ queue().length }}</span>
        <button mat-button (click)="sync()"><mat-icon>sync</mat-icon> {{ 'pos.syncNow' | t }}</button>
      }
      <button mat-icon-button (click)="printerSettings()" [matTooltip]="'printer.title' | t"><mat-icon>print</mat-icon></button>
      @if (policy().canOverride) { <button mat-icon-button (click)="myPin()" [matTooltip]="'pos.myPin' | t"><mat-icon>pin</mat-icon></button> }
      @if (canManage) { <button mat-stroked-button (click)="editRegister(null)"><mat-icon>add</mat-icon> {{ 'pos.newRegister' | t }}</button> }
    </div>

    @if (!shift()) {
      <div class="registers">
        @for (r of registers(); track r.id) {
          <mat-card class="register">
            <mat-card-header>
              <mat-card-title>{{ r | bi }}</mat-card-title>
              <mat-card-subtitle class="ltr">{{ r.code }}</mat-card-subtitle>
            </mat-card-header>
            <mat-card-actions>
              @if (r.openShiftId) {
                <button mat-flat-button (click)="resume(r)">{{ 'pos.resume' | t }}</button>
              } @else if (r.isActive) {
                <button mat-flat-button (click)="open(r)">{{ 'pos.openShift' | t }}</button>
              }
              <button mat-button (click)="history(r)">{{ 'pos.shifts' | t }}</button>
              @if (canManage) { <button mat-button (click)="editRegister(r)">{{ 'app.edit' | t }}</button> }
            </mat-card-actions>
          </mat-card>
        }
        @if (!registers().length) { <p class="secondary">{{ 'pos.noRegisters' | t }}</p> }
      </div>
    } @else {
      <div class="shift-bar">
        <span><b>{{ register()! | bi }}</b> · <span class="ltr">{{ shift()!.shiftNo }}</span></span>
        <span class="spacer"></span>
        <button mat-button (click)="refund()"><mat-icon>undo</mat-icon> {{ 'pos.refund' | t }}</button>
        <button mat-button (click)="zReport(shift()!.id)"><mat-icon>summarize</mat-icon> {{ 'pos.xReport' | t }}</button>
        <button mat-stroked-button (click)="close()"><mat-icon>lock</mat-icon> {{ 'pos.closeShift' | t }}</button>
        <button mat-button (click)="leave()">{{ 'app.back' | t }}</button>
      </div>
      <div class="till">
        <section class="catalog">
          <mat-form-field appearance="outline" class="search">
            <mat-icon matPrefix>qr_code_scanner</mat-icon>
            <input matInput [formControl]="search" [placeholder]="'pos.scan' | t" (keydown.enter)="scan()" autocomplete="off" #scanBox />
          </mat-form-field>
          <div class="grid">
            @for (i of visible(); track i.id) {
              <button class="tile" (click)="add(i)">
                <span class="name">{{ i | bi }}</span>
                <span class="price ltr">{{ i.salesPrice | money }}</span>
                @if (i.onHand != null) {
                  <span [class]="'stock stock-' + tileLevel(i)">
                    @if (i.onHand > 0) { {{ 'so.onHand' | t }} <span class="ltr">{{ boxes(i) }}</span> } @else { {{ 'stock.outOfStock' | t }} }</span>
                }
              </button>
            }
          </div>
        </section>
        <section class="cart">
          <div class="lines">
            @for (l of cart(); track l.item.id + (l.unit?.uomId ?? ''); let idx = $index) {
              <div class="line">
                <div class="what">
                  <div>{{ l.item | bi }} @if (l.unit) { <span class="chip info ltr">{{ l.unit.uomCode }}</span> }</div>
                  <div class="secondary ltr">{{ price(l) | money }} × {{ l.quantity }} @if (l.discountPercent) { − {{ l.discountPercent }}% }</div>
                </div>
                <div class="qty">
                  <button mat-icon-button (click)="change(idx, -1)"><mat-icon>remove</mat-icon></button>
                  <span class="ltr">{{ l.quantity }}</span>
                  <button mat-icon-button (click)="change(idx, 1)"><mat-icon>add</mat-icon></button>
                  <button mat-icon-button [matMenuTriggerFor]="lineMenu"><mat-icon>more_vert</mat-icon></button>
                  <mat-menu #lineMenu="matMenu">
                    @if (l.item.units?.length) {
                      <button mat-menu-item (click)="sellBy(idx, null)" [disabled]="!l.unit">{{ 'units.sellBy' | t }}: {{ 'units.piece' | t }}</button>
                      @for (u of l.item.units; track u.uomId) {
                        <button mat-menu-item (click)="sellBy(idx, u)" [disabled]="l.unit?.uomId === u.uomId">
                          {{ 'units.sellBy' | t }}: <span class="ltr">{{ u.uomCode }} ({{ u.factor }}) · {{ u.salesPrice | money }}</span></button>
                      }
                    }
                    <button mat-menu-item (click)="discount(idx)">{{ 'pos.discount' | t }}</button>
                    <button mat-menu-item (click)="changePrice(idx)">{{ 'pos.changePrice' | t }}</button>
                    <button mat-menu-item (click)="remove(idx)">{{ 'app.delete' | t }}</button>
                  </mat-menu>
                </div>
                <div class="amount ltr">{{ amount(l) | money }}</div>
              </div>
            }
            @if (!cart().length) { <p class="secondary empty">{{ 'pos.emptyCart' | t }}</p> }
          </div>
          <div class="totals">
            <div class="total"><span>{{ 'app.total' | t }}</span><span class="ltr">{{ total() | money }}</span></div>
            <div class="pay">
              <mat-form-field appearance="outline"><mat-label>{{ 'pos.cash' | t }}</mat-label>
                <input matInput type="number" class="ltr" [formControl]="cash" /></mat-form-field>
              <mat-form-field appearance="outline"><mat-label>{{ 'pos.card' | t }}</mat-label>
                <input matInput type="number" class="ltr" [formControl]="card" /></mat-form-field>
            </div>
            <div class="quick">
              <button mat-stroked-button (click)="exact('cash')">{{ 'pos.exactCash' | t }}</button>
              <button mat-stroked-button (click)="exact('card')">{{ 'pos.allCard' | t }}</button>
              @for (n of [50, 100, 200, 500]; track n) { <button mat-button class="ltr" (click)="cashGiven(n)">{{ n }}</button> }
            </div>
            <div class="change" [class.short]="due() > 0">
              @if (due() > 0) { <span>{{ 'pos.due' | t }}</span><span class="ltr">{{ due() | money }}</span> }
              @else { <span>{{ 'pos.change' | t }}</span><span class="ltr">{{ change_() | money }}</span> }
            </div>
            <button mat-flat-button class="pay-btn" [disabled]="!cart().length || due() > 0 || busy()" (click)="pay()">{{ 'pos.pay' | t }}</button>
            <button mat-stroked-button class="account-btn" [disabled]="!cart().length || busy()" (click)="onAccount()"><mat-icon>menu_book</mat-icon> {{ 'pos.onAccount' | t }}</button>
            @if (last(); as s) {
              <div class="last">
                <span>{{ 'pos.lastReceipt' | t }}: @if (s.offline) { <b>{{ 'pos.provisional' | t }}</b> } @else { <b class="ltr">{{ s.receiptNo }}</b> }</span>
                <button mat-button (click)="print(s)"><mat-icon>print</mat-icon> {{ 'pos.print' | t }}</button>
              </div>
            }
            @if (low().length) {
              <div class="low-note stock-low">
                <mat-icon>inventory_2</mat-icon>
                <span class="text">{{ lowStock.text(low()) }}</span>
                @if (lowStock.canReorder()) { <button mat-button (click)="lowStock.openReorder()">{{ 'stock.reorderNow' | t }}</button> }
                <button mat-icon-button (click)="low.set([])" [attr.aria-label]="'app.close' | t"><mat-icon>close</mat-icon></button>
              </div>
            }
          </div>
        </section>
      </div>
    }
    @for (q of failed(); track q.clientId) {
      <p class="failed">{{ 'pos.syncFailed' | t }} <span class="ltr">{{ q.total | money }}</span>: {{ q.error }}
        @if (q.errorCode?.startsWith('sal.pos_approval') || q.errorCode === 'sal.pos_needs_approval') {
          <button mat-button (click)="askManager(q)">{{ 'pos.askManager' | t }}</button>
        }
        <button mat-button (click)="discard(q)">{{ 'pos.discard' | t }}</button></p>
    }
  `,
  styles: `
    .spacer { flex: 1; }
    .secondary { color: var(--mat-sys-on-surface-variant); }
    .registers { display: grid; grid-template-columns: repeat(auto-fill, minmax(260px, 1fr)); gap: 16px; }
    .shift-bar { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin-block-end: 12px; }
    .till { display: grid; grid-template-columns: minmax(0, 3fr) minmax(300px, 2fr); gap: 16px; }
    /* minmax(0, …): on a phone the column must not grow to its widest content and push the till off the screen. */
    @media (max-width: 900px) { .till { grid-template-columns: minmax(0, 1fr); } }
    .search { width: 100%; }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(130px, 1fr)); gap: 8px; max-height: 60vh; overflow: auto; }
    .tile { display: flex; flex-direction: column; justify-content: space-between; gap: 6px; min-height: 84px; padding: 10px; text-align: start;
      border: 1px solid var(--mat-sys-outline-variant); border-radius: 12px; background: var(--mat-sys-surface-container-low); cursor: pointer;
      color: inherit; font: inherit; }
    .tile:hover { background: var(--mat-sys-surface-container-high); }
    .tile .price { font-weight: 600; color: var(--mat-sys-primary); }
    .cart { display: flex; flex-direction: column; border: 1px solid var(--mat-sys-outline-variant); border-radius: 12px; padding: 12px; }
    .lines { flex: 1; max-height: 40vh; overflow: auto; }
    .line { display: grid; grid-template-columns: 1fr auto auto; align-items: center; gap: 8px; padding-block: 6px;
      border-block-end: 1px solid var(--mat-sys-outline-variant); }
    .qty { display: flex; align-items: center; }
    .amount { min-width: 80px; text-align: end; font-weight: 600; }
    .empty { padding: 16px; text-align: center; }
    .total, .change { display: flex; justify-content: space-between; font-size: 1.4rem; font-weight: 600; margin-block: 12px; }
    .change { font-size: 1.1rem; }
    .change.short { color: var(--mat-sys-error); }
    .pay { display: flex; flex-wrap: wrap; gap: 0 8px; }
    .pay mat-form-field { flex: 1 1 120px; min-width: 0; }
    .quick { display: flex; flex-wrap: wrap; gap: 4px; }
    .pay-btn { width: 100%; height: 56px; font-size: 1.2rem; }
    .account-btn { width: 100%; margin-top: 8px; }
    .last { display: flex; align-items: center; justify-content: space-between; margin-block-start: 8px; }
    .failed { color: var(--mat-sys-error); }
    .tile .stock { font-size: 12px; color: var(--mat-sys-on-surface-variant); }
    .tile .stock.stock-out { color: var(--mat-sys-error); font-weight: 600; }
    .tile .stock.stock-low { color: #9a5b00; font-weight: 600; }
    .low-note { display: flex; align-items: center; gap: 8px; margin-block-start: 8px; padding: 4px 8px; border-radius: 8px; background: #fff1d6; }
    .low-note .text { flex: 1; }
  `
})
export class PosPage implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  protected readonly lowStock = inject(LowStockNotice);
  /** Items the last sale left at or below their reorder level; stays until the next sale or until closed. */
  protected readonly low = signal<LowItem[]>([]);
  private readonly i18n = inject(I18nService);
  private readonly receipt = inject(ReceiptService);
  protected readonly canManage = inject(AuthService).can('sal.pos:manage');
  /** Kept on the device so the limit also holds offline. */
  protected readonly policy = signal<Policy>(readStore<Policy>(POLICY_KEY, { maxDiscountPercent: 10, canOverride: false }));
  protected readonly registers = signal<Register[]>(readStore<Register[]>(REGISTERS_KEY, []));
  protected readonly catalog = signal<CatalogItem[]>(readStore<CatalogItem[]>(CATALOG_KEY, []));
  protected readonly shift = signal<Shift | null>(null);
  protected readonly register = computed(() => this.registers().find((r) => r.id === this.shift()?.registerId) ?? null);
  protected readonly cart = signal<CartLine[]>([]);
  protected readonly queue = signal<QueuedSale[]>(readStore<QueuedSale[]>(QUEUE_KEY, []));
  protected readonly failed = computed(() => this.queue().filter((q) => q.error));
  protected readonly last = signal<Sale | null>(null);
  protected readonly busy = signal(false);
  protected readonly search = new FormControl('', { nonNullable: true });
  protected readonly cash = new FormControl<number | null>(null);
  protected readonly card = new FormControl<number | null>(null);
  private readonly term = signal('');
  private readonly paid = signal({ cash: 0, card: 0 });
  protected readonly visible = computed(() => {
    const term = this.term().trim().toLowerCase();
    const list = this.catalog();
    return (term ? list.filter((i) => i.sku.toLowerCase().includes(term) || i.nameEn.toLowerCase().includes(term) || i.nameAr.includes(term)) : list).slice(0, 120);
  });
  protected readonly total = computed(() => round2(this.cart().reduce((s, l) => s + lineAmount(l), 0)));
  protected readonly due = computed(() => round2(this.total() - this.paid().cash - this.paid().card));
  protected readonly change_ = computed(() => -this.due());
  private timer?: ReturnType<typeof setInterval>;
  private syncing = false;
  private readonly onOnline = () => this.sync();

  ngOnInit(): void {
    this.search.valueChanges.subscribe((v) => this.term.set(v));
    this.cash.valueChanges.subscribe((v) => this.paid.update((p) => ({ ...p, cash: Number(v) || 0 })));
    this.card.valueChanges.subscribe((v) => this.paid.update((p) => ({ ...p, card: Number(v) || 0 })));
    this.load();
    window.addEventListener('online', this.onOnline);
    this.timer = setInterval(() => this.sync(), 30_000);
    this.sync();
  }

  ngOnDestroy(): void {
    window.removeEventListener('online', this.onOnline);
    clearInterval(this.timer);
  }

  private load(): void {
    this.api.get<Register[]>('sales/pos/registers').subscribe({
      next: (r) => {
        this.registers.set(r);
        writeStore(REGISTERS_KEY, r);
      },
      // Offline: the tills kept from last time.
      error: (e) => (isOffline(e) ? undefined : this.notify.error(e))
    });
    this.api.get<Policy>('sales/pos/policy').subscribe({
      next: (p) => {
        this.policy.set(p);
        writeStore(POLICY_KEY, p);
      },
      error: (e) => (isOffline(e) ? undefined : this.notify.error(e))
    });
    this.api.get<CatalogItem[]>('sales/pos/catalog').subscribe({
      next: (c) => {
        this.catalog.set(c);
        writeStore(CATALOG_KEY, c);
      },
      // Offline: keep the cached catalogue.
      error: (e) => (isOffline(e) ? undefined : this.notify.error(e))
    });
  }

  protected amount = lineAmount;
  protected price = priceOf;

  protected open(r: Register): void {
    this.dialog
      .open(OpenShiftDialog, { width: '360px' })
      .afterClosed()
      .pipe(
        filter((v): v is number => v != null),
        switchMap((openingFloat) => this.api.post<Shift>('sales/pos/shifts', { registerId: r.id, openingFloat }))
      )
      .subscribe({
        next: (s) => {
          this.setShift(s);
          this.registers.update((list) => list.map((x) => (x.id === r.id ? { ...x, openShiftId: s.id } : x)));
        },
        error: (e) => this.notify.error(e)
      });
  }

  protected resume(r: Register): void {
    this.api.get<Shift[]>('sales/pos/shifts', { registerId: r.id }).subscribe({
      next: (list) => this.setShift(list.find((s) => s.id === r.openShiftId) ?? null),
      error: (e) => {
        if (!isOffline(e) || !r.openShiftId) return this.notify.error(e);
        // Offline: carry on with the open shift (the copy this device kept, or just its id; sales only need that).
        const kept = readStore<Shift | null>(SHIFT_KEY, null);
        this.shift.set(kept?.id === r.openShiftId ? kept : {
          id: r.openShiftId, shiftNo: '', registerId: r.id, registerCode: r.code, openedAt: '', openingFloat: 0, status: 'Open', closedAt: null,
          businessDate: '', expectedCash: null, countedCash: null, difference: null
        });
      }
    });
  }

  private setShift(s: Shift | null): void {
    this.shift.set(s);
    writeStore(SHIFT_KEY, s);
  }

  protected leave(): void {
    this.shift.set(null);
    this.cart.set([]);
    this.load();
  }

  protected scan(): void {
    const code = this.search.value.trim();
    if (!code) return;
    const lower = code.toLowerCase();
    const hit = this.catalog().find((i) => i.barcode === code || i.sku.toLowerCase() === lower) ?? (this.visible().length === 1 ? this.visible()[0] : null);
    // A box has its own barcode: scanning it adds one box at the box price.
    const box = hit ? null : this.catalog().flatMap((i) => (i.units ?? []).filter((u) => u.barcode === code).map((u) => ({ item: i, unit: u })))[0];
    if (hit || box) {
      if (box) this.add(box.item, box.unit);
      else this.add(hit!);
      this.search.setValue('');
    } else this.notify.info(this.i18n.t('pos.notFound'));
  }

  /** Red when the cart already takes all of it, amber at or below the reorder level after the cart. */
  protected tileLevel(item: CatalogItem): string {
    const inCart = this.cart().filter((l) => l.item.id === item.id).reduce((sum, l) => sum + piecesOf(l), 0);
    return stockLevel(item.onHand ?? 0, item.reorderLevel, inCart);
  }

  /** Stock in boxes and pieces ("4 BOX12 + 10"), or pieces alone. */
  protected boxes(item: CatalogItem): string {
    return inBoxes(item.onHand ?? 0, item.uomCode, item.units ?? []);
  }

  /** Switches a line between pieces and a box; the box's price applies (a price typed for the line is dropped). */
  protected sellBy(idx: number, unit: CatalogUnit | null): void {
    this.cart.update((list) => {
      const line = list[idx];
      const other = list.findIndex((l, i) => i !== idx && sameLine(l, line.item, unit));
      if (other >= 0) return list.map((l, i) => (i === other ? { ...l, quantity: l.quantity + line.quantity } : l)).filter((_, i) => i !== idx);
      return list.map((l, i) => (i === idx ? { ...l, unit, price: undefined } : l));
    });
  }

  /** Takes a sale off the stock this device knows (also offline) and notes what is now running low. */
  private sold(lines: { itemId: string | null; quantity: number }[]): void {
    const taken = new Map<string, number>();
    for (const l of lines) if (l.itemId) taken.set(l.itemId, (taken.get(l.itemId) ?? 0) + l.quantity);
    const catalog = this.catalog().map((i) => (i.onHand != null && taken.has(i.id) ? { ...i, onHand: round2(i.onHand - taken.get(i.id)!) } : i));
    this.catalog.set(catalog);
    writeStore(CATALOG_KEY, catalog);
    this.low.set(catalog
      .filter((i) => taken.has(i.id) && i.onHand != null && stockLevel(i.onHand, i.reorderLevel) !== 'ok')
      .map((i) => ({ nameEn: i.nameEn, nameAr: i.nameAr, left: i.onHand! })));
  }

  protected add(item: CatalogItem, unit: CatalogUnit | null = null): void {
    this.cart.update((list) => {
      const idx = list.findIndex((l) => sameLine(l, item, unit));
      return idx < 0 ? [...list, { item, unit, quantity: 1, discountPercent: 0 }] : list.map((l, i) => (i === idx ? { ...l, quantity: l.quantity + 1 } : l));
    });
  }

  protected change(idx: number, delta: number): void {
    this.cart.update((list) => list.map((l, i) => (i === idx ? { ...l, quantity: l.quantity + delta } : l)).filter((l) => l.quantity > 0));
  }

  protected remove(idx: number): void {
    this.cart.update((list) => list.filter((_, i) => i !== idx));
  }

  protected discount(idx: number): void {
    const value = prompt(this.i18n.t('pos.discountPrompt'), String(this.cart()[idx].discountPercent));
    const pct = Number(value);
    if (value == null || isNaN(pct) || pct < 0 || pct > 100) return;
    this.cart.update((list) => list.map((l, i) => (i === idx ? { ...l, discountPercent: pct } : l)));
  }

  protected changePrice(idx: number): void {
    const value = prompt(this.i18n.t('pos.pricePrompt'), String(priceOf(this.cart()[idx])));
    const price = Number(value);
    if (value == null || value.trim() === '' || isNaN(price) || price < 0) return;
    this.cart.update((list) => list.map((l, i) => (i === idx ? { ...l, price: price === listPriceOf(l) ? undefined : round2(price) } : l)));
  }

  /**
   * A sale past the cashier limit needs a manager's PIN (online only: the server checks it). Emits the approval id, null when
   * none is needed, and nothing when the cashier cancels or there is no connection.
   */
  private approval(shiftId: string, lines: { listPrice: number; quantity: number; amount: number }[]): Observable<string | null> {
    const policy = this.policy();
    const { cut, over } = measure(lines, policy.maxDiscountPercent);
    if (!over || policy.canOverride) return of(null);
    if (!navigator.onLine) {
      this.notify.info(this.i18n.t('pos.approvalOffline', { max: policy.maxDiscountPercent }));
      return EMPTY;
    }
    return this.dialog
      .open(ApprovalDialog, { data: { shiftId, discount: cut, max: policy.maxDiscountPercent }, width: '400px' })
      .afterClosed()
      .pipe(filter((id): id is string => !!id));
  }

  private cartLines() {
    return this.cart().map((l) => ({ listPrice: listPriceOf(l), quantity: l.quantity, amount: lineAmount(l) }));
  }

  /** A queued sale the server refused for want of approval: the manager approves it now and it is sent again. */
  protected askManager(q: QueuedSale): void {
    const lines = q.lines.flatMap((l) => {
      const item = this.catalog().find((i) => i.id === l.itemId);
      const listPrice = l.uomId ? item?.units?.find((u) => u.uomId === l.uomId)?.salesPrice : item?.salesPrice;
      return listPrice != null ? [{ listPrice, quantity: l.quantity, amount: round2(l.quantity * l.unitPrice * (1 - l.discountPercent / 100)) }] : [];
    });
    const { cut } = measure(lines, this.policy().maxDiscountPercent);
    this.dialog
      .open(ApprovalDialog, { data: { shiftId: q.shiftId, discount: cut, max: this.policy().maxDiscountPercent }, width: '400px' })
      .afterClosed()
      .pipe(filter((id): id is string => !!id))
      .subscribe((approvalId) => {
        this.queue.update((list) => list.map((x) => (x.clientId === q.clientId ? { ...x, approvalId, error: undefined, errorCode: undefined } : x)));
        writeStore(QUEUE_KEY, this.queue());
        this.sync();
      });
  }

  protected myPin(): void {
    this.dialog.open(PinDialog, { width: '360px' }).afterClosed().pipe(filter((saved) => !!saved)).subscribe(() => this.notify.success('pos.pinSaved'));
  }

  protected exact(kind: 'cash' | 'card'): void {
    this.cash.setValue(kind === 'cash' ? this.total() : 0);
    this.card.setValue(kind === 'card' ? this.total() : 0);
  }

  protected cashGiven(n: number): void {
    this.cash.setValue(n);
  }

  /** A regular customer takes the goods now and pays later (needs a connection: it becomes a sales invoice). */
  protected onAccount(): void {
    const shift = this.shift();
    if (!shift) return;
    this.approval(shift.id, this.cartLines()).pipe(
      switchMap((approvalId) => this.dialog.open(CustomerPickDialog, { width: '420px' }).afterClosed().pipe(
        filter((c): c is { id: string; nameEn: string } => !!c),
        map((customer) => ({ customer, approvalId }))))
    ).subscribe(({ customer, approvalId }) => {
      this.busy.set(true);
      this.api.post<Sale>('sales/pos/sales', {
        clientId: crypto.randomUUID(), shiftId: shift.id, soldAt: new Date().toISOString(), cash: 0, card: 0, customerId: customer.id,
        customerName: customer.nameEn, customerPhone: null, approvalId,
        lines: this.cartRequest()
      }).subscribe({
        next: (s) => {
          this.busy.set(false);
          this.done(s);
          this.notify.success('pos.onAccountDone');
        },
        error: (e) => {
          this.busy.set(false);
          this.notify.error(e);
        }
      });
    });
  }

  protected pay(): void {
    const shift = this.shift();
    if (!shift) return;
    this.approval(shift.id, this.cartLines()).subscribe((approvalId) => this.send(shift, approvalId));
  }

  private send(shift: Shift, approvalId: string | null): void {
    const sale: QueuedSale = {
      clientId: crypto.randomUUID(),
      shiftId: shift.id,
      soldAt: new Date().toISOString(), // an instant (when the till rang it up), not a calendar date
      lines: this.cartRequest(),
      approvalId,
      cash: this.paid().cash,
      card: this.paid().card,
      customerName: null,
      customerPhone: null,
      total: this.total()
    };
    this.busy.set(true);
    this.api.post<Sale>('sales/pos/sales', sale).subscribe({
      next: (s) => {
        this.busy.set(false);
        this.done(s);
      },
      error: (e) => {
        this.busy.set(false);
        if (isOffline(e)) {
          this.enqueue(sale);
          this.sold(this.cart().map((l) => ({ itemId: l.item.id, quantity: piecesOf(l) })));
          this.last.set(this.provisional(sale));
          if (this.receipt.settings().autoPrint) this.print(this.last()!);
          this.reset();
          this.notify.info(this.i18n.t('pos.savedOffline'));
        } else this.notify.error(e);
      }
    });
  }

  /** The cart as the server takes it: quantity and price per the line's unit (piece or box). */
  private cartRequest() {
    return this.cart().map((l) => ({ itemId: l.item.id, quantity: l.quantity, unitPrice: priceOf(l), discountPercent: l.discountPercent, uomId: l.unit?.uomId ?? null }));
  }

  /** What the customer's receipt shows for a sale kept on this device until the connection is back. */
  private provisional(q: QueuedSale): Sale {
    const lines = this.cart().map((l, i) => ({
      id: String(i), lineNo: i + 1, itemId: l.item.id, sku: l.item.sku, nameEn: l.item.nameEn, nameAr: l.item.nameAr, quantity: piecesOf(l),
      unitPrice: priceOf(l) / (l.unit?.factor ?? 1), discountPercent: l.discountPercent, amount: lineAmount(l), refundable: 0,
      uomCode: l.unit?.uomCode ?? null, uomQuantity: l.unit ? l.quantity : null, uomPrice: l.unit ? priceOf(l) : null
    }));
    const gross = round2(this.cart().reduce((sum, l) => sum + l.quantity * priceOf(l), 0));
    return {
      id: q.clientId, receiptNo: '', soldAt: q.soldAt, customerName: null, total: q.total, discountTotal: round2(gross - q.total),
      cashAmount: q.cash, cardAmount: q.card, changeGiven: Math.max(0, round2(q.cash + q.card - q.total)), isRefund: false, lines, offline: true
    };
  }

  private done(s: Sale): void {
    this.last.set(s);
    if (!s.isRefund) this.sold(s.lines);
    if (this.receipt.settings().autoPrint) this.print(s);
    this.reset();
    if (s.changeGiven > 0) this.notify.info(`${this.i18n.t('pos.change')}: ${s.changeGiven.toFixed(2)}`);
  }

  private reset(): void {
    this.cart.set([]);
    this.cash.setValue(null);
    this.card.setValue(null);
  }

  private enqueue(sale: QueuedSale): void {
    this.queue.update((q) => [...q, sale]);
    writeStore(QUEUE_KEY, this.queue());
  }

  /** Sends queued sales in order; stops at the first network failure and keeps business errors for the cashier to see. */
  protected sync(): void {
    // One sale at a time: the timer, the "online" event and the button must not send the same sale twice at once.
    if (this.syncing) return;
    const next = this.queue().find((q) => !q.error);
    if (!next) return;
    this.syncing = true;
    this.api.post<Sale>('sales/pos/sales', next).subscribe({
      next: () => {
        this.syncing = false;
        this.queue.update((q) => q.filter((x) => x.clientId !== next.clientId));
        writeStore(QUEUE_KEY, this.queue());
        this.sync();
      },
      error: (e) => {
        this.syncing = false;
        if (isOffline(e)) return;
        this.queue.update((q) => q.map((x) => (x.clientId === next.clientId ? { ...x, error: this.notify.message(e), errorCode: problemOf(e)?.code } : x)));
        writeStore(QUEUE_KEY, this.queue());
        this.sync();
      }
    });
  }

  protected discard(q: QueuedSale): void {
    if (!confirm(this.i18n.t('pos.discardConfirm'))) return;
    this.queue.update((list) => list.filter((x) => x.clientId !== q.clientId));
    writeStore(QUEUE_KEY, this.queue());
  }

  protected refund(): void {
    const shift = this.shift();
    if (!shift) return;
    this.dialog
      .open(RefundDialog, { data: { shiftId: shift.id }, width: '620px' })
      .afterClosed()
      .pipe(filter((s): s is Sale => !!s))
      .subscribe((s) => {
        this.last.set(s);
        this.notify.success();
      });
  }

  protected close(): void {
    const shift = this.shift();
    if (!shift) return;
    if (this.queue().some((q) => q.shiftId === shift.id)) {
      this.notify.info(this.i18n.t('pos.syncFirst'));
      return;
    }
    this.dialog
      .open(CloseShiftDialog, { width: '380px' })
      .afterClosed()
      .pipe(
        filter((v): v is { countedCash: number; notes: string } => !!v),
        switchMap((v) => this.api.post<ZReport>(`sales/pos/shifts/${shift.id}/close`, v))
      )
      .subscribe({
        next: (z) => {
          writeStore(SHIFT_KEY, null);
          this.leave();
          this.dialog.open(ZReportDialog, { data: z, width: '560px' });
        },
        error: (e) => this.notify.error(e)
      });
  }

  protected zReport(shiftId: string): void {
    this.api.get<ZReport>(`sales/pos/shifts/${shiftId}/z-report`).subscribe({
      next: (z) => this.dialog.open(ZReportDialog, { data: z, width: '560px' }),
      error: (e) => this.notify.error(e)
    });
  }

  protected history(r: Register): void {
    this.api.get<Shift[]>('sales/pos/shifts', { registerId: r.id }).subscribe({
      next: (shifts) =>
        this.dialog
          .open(ShiftHistoryDialog, { data: { register: r, shifts }, width: '720px' })
          .afterClosed()
          .pipe(filter((id): id is string => !!id))
          .subscribe((id) => this.zReport(id)),
      error: (e) => this.notify.error(e)
    });
  }

  protected editRegister(r: Register | null): void {
    this.dialog
      .open(RegisterDialog, { data: r, width: '520px' })
      .afterClosed()
      .pipe(filter((saved) => !!saved))
      .subscribe(() => {
        this.notify.success();
        this.load();
      });
  }

  protected printerSettings(): void {
    this.dialog.open(PrinterSettingsDialog, { width: '440px' });
  }

  protected print(s: Sale): void {
    const ar = this.i18n.isArabic();
    const reg = this.register();
    const t = (k: Parameters<I18nService['t']>[0]) => this.i18n.t(k);
    const paid = [
      { label: t('pos.cash'), amount: s.cashAmount },
      { label: t('pos.card'), amount: s.cardAmount },
      { label: t('pos.change'), amount: s.changeGiven }
    ].filter((x) => x.amount > 0);
    this.receipt.print({
      title: s.isRefund ? t('pos.refund') : t('pos.receipt'),
      number: s.offline ? undefined : s.receiptNo,
      meta: [new Date(s.soldAt).toLocaleString('en-GB'), ...(reg ? [ar ? reg.nameAr : reg.nameEn] : []), ...(s.customerName ? [s.customerName] : [])],
      lines: s.lines.map((l) => ({
        text: ar ? l.nameAr : l.nameEn,
        sub: (l.uomCode && l.uomQuantity != null && l.uomPrice != null
          ? `${l.uomQuantity} ${l.uomCode} × ${l.uomPrice.toFixed(2)}`
          : `${l.quantity} × ${l.unitPrice.toFixed(2)}`) + (l.discountPercent ? ` −${l.discountPercent}%` : ''),
        amount: l.amount
      })),
      totals: [
        ...(s.discountTotal > 0 ? [{ label: t('pos.discount'), amount: s.discountTotal }] : []),
        { label: t('app.total'), amount: s.total, bold: true },
        ...paid
      ],
      note: s.offline ? t('pos.provisionalNote') : undefined
    });
  }
}

@Component({
  selector: 'app-open-shift-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'pos.openShift' | t }}</h2>
    <mat-dialog-content>
      <mat-form-field appearance="outline" class="full"><mat-label>{{ 'pos.float' | t }}</mat-label>
        <input matInput type="number" class="ltr" [formControl]="float" /></mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="float.invalid" (click)="ref.close(float.value)">{{ 'pos.openShift' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `.full { width: 100%; }`
})
class OpenShiftDialog {
  protected readonly ref = inject(MatDialogRef<OpenShiftDialog>);
  protected readonly float = new FormControl(0, { nonNullable: true, validators: [Validators.required, Validators.min(0)] });
}

@Component({
  selector: 'app-close-shift-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'pos.closeShift' | t }}</h2>
    <mat-dialog-content [formGroup]="form">
      <p class="secondary">{{ 'pos.countHint' | t }}</p>
      <mat-form-field appearance="outline" class="full"><mat-label>{{ 'pos.counted' | t }}</mat-label>
        <input matInput type="number" class="ltr" formControlName="countedCash" /></mat-form-field>
      <mat-form-field appearance="outline" class="full"><mat-label>{{ 'app.notes' | t }}</mat-label>
        <input matInput formControlName="notes" /></mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid" (click)="ref.close(form.getRawValue())">{{ 'pos.closeShift' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `.full { width: 100%; } .secondary { color: var(--mat-sys-on-surface-variant); }`
})
class CloseShiftDialog {
  protected readonly ref = inject(MatDialogRef<CloseShiftDialog>);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    countedCash: [null as unknown as number, [Validators.required, Validators.min(0)]],
    notes: ['']
  });
}

@Component({
  selector: 'app-z-report-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ (z.shift.status === 'Closed' ? 'pos.zReport' : 'pos.xReport') | t }} · <span class="ltr">{{ z.shift.shiftNo }}</span></h2>
    <mat-dialog-content>
      <table class="kv">
        <tr><td>{{ 'pos.salesCount' | t }}</td><td class="ltr">{{ z.salesCount }}</td></tr>
        <tr><td>{{ 'pos.gross' | t }}</td><td class="ltr">{{ z.grossSales | money }}</td></tr>
        <tr><td>{{ 'pos.discounts' | t }}</td><td class="ltr">{{ z.discounts | money }}</td></tr>
        <tr><td>{{ 'pos.refunds' | t }} ({{ z.refundCount }})</td><td class="ltr">{{ z.refunds | money }}</td></tr>
        <tr class="b"><td>{{ 'pos.net' | t }}</td><td class="ltr">{{ z.netSales | money }}</td></tr>
        <tr><td>{{ 'pos.cash' | t }}</td><td class="ltr">{{ z.cashSales | money }}</td></tr>
        <tr><td>{{ 'pos.card' | t }}</td><td class="ltr">{{ z.cardSales | money }}</td></tr>
        <tr><td>{{ 'pos.float' | t }}</td><td class="ltr">{{ z.openingFloat | money }}</td></tr>
        <tr class="b"><td>{{ 'pos.expected' | t }}</td><td class="ltr">{{ z.expectedCash | money }}</td></tr>
        @if (z.countedCash != null) {
          <tr><td>{{ 'pos.counted' | t }}</td><td class="ltr">{{ z.countedCash | money }}</td></tr>
          <tr class="b" [class.neg]="(z.difference ?? 0) < 0"><td>{{ 'pos.difference' | t }}</td><td class="ltr">{{ z.difference | money }}</td></tr>
        }
        <tr><td>{{ 'pos.grossProfit' | t }}</td><td class="ltr">{{ z.netSales - z.cost | money }}</td></tr>
      </table>
      @if (z.topItems.length) {
        <h3>{{ 'pos.topItems' | t }}</h3>
        <table class="kv">
          @for (i of z.topItems; track i.itemId) { <tr><td>{{ i | bi }} <span class="ltr secondary">× {{ i.quantity }}</span></td><td class="ltr">{{ i.amount | money }}</td></tr> }
        </table>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end"><button mat-button mat-dialog-close>{{ 'app.close' | t }}</button></mat-dialog-actions>
  `,
  styles: `
    .kv { width: 100%; border-collapse: collapse; }
    .kv td { padding: 4px 0; border-block-end: 1px solid var(--mat-sys-outline-variant); }
    .kv td:last-child { text-align: end; }
    .b td { font-weight: 600; }
    .neg td { color: var(--mat-sys-error); }
    .secondary { color: var(--mat-sys-on-surface-variant); }
  `
})
class ZReportDialog {
  protected readonly z = inject<ZReport>(MAT_DIALOG_DATA);
}

@Component({
  selector: 'app-shift-history-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'pos.shifts' | t }} · {{ data.register | bi }}</h2>
    <mat-dialog-content>
      <table class="line-editor">
        <thead><tr><th>{{ 'pos.shift' | t }}</th><th>{{ 'app.date' | t }}</th><th>{{ 'app.status' | t }}</th>
          <th class="num">{{ 'pos.expected' | t }}</th><th class="num">{{ 'pos.difference' | t }}</th><th></th></tr></thead>
        <tbody>
          @for (s of data.shifts; track s.id) {
            <tr>
              <td class="ltr">{{ s.shiftNo }}</td>
              <td class="ltr">{{ s.businessDate | d }}</td>
              <td><span class="chip" [class.info]="s.status === 'Open'">{{ 'pos.status.' + s.status | t }}</span></td>
              <td class="num ltr">{{ s.expectedCash | money }}</td>
              <td class="num ltr">{{ s.difference | money }}</td>
              <td><button mat-button (click)="ref.close(s.id)">{{ 'pos.zReport' | t }}</button></td>
            </tr>
          }
        </tbody>
      </table>
      @if (!data.shifts.length) { <p class="secondary">{{ 'app.none' | t }}</p> }
    </mat-dialog-content>
    <mat-dialog-actions align="end"><button mat-button mat-dialog-close>{{ 'app.close' | t }}</button></mat-dialog-actions>
  `,
  styles: `.secondary { color: var(--mat-sys-on-surface-variant); }`
})
class ShiftHistoryDialog {
  protected readonly ref = inject(MatDialogRef<ShiftHistoryDialog>);
  protected readonly data = inject<{ register: Register; shifts: Shift[] }>(MAT_DIALOG_DATA);
}

@Component({
  selector: 'app-refund-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'pos.refund' | t }}</h2>
    <mat-dialog-content>
      <div class="find">
        <mat-form-field appearance="outline"><mat-label>{{ 'pos.receipt' | t }}</mat-label>
          <input matInput class="ltr" [formControl]="receipt" (keydown.enter)="find()" /></mat-form-field>
        <button mat-stroked-button (click)="find()">{{ 'app.search' | t }}</button>
      </div>
      @if (sale(); as s) {
        <table class="line-editor">
          <thead><tr><th>{{ 'pos.item' | t }}</th><th class="num">{{ 'pos.sold' | t }}</th><th class="num">{{ 'pos.refundable' | t }}</th>
            <th class="num">{{ 'pos.refundQty' | t }}</th></tr></thead>
          <tbody>
            @for (l of s.lines; track l.id; let i = $index) {
              <tr>
                <td>{{ l | bi }}</td>
                <td class="num ltr">{{ l.quantity }}</td>
                <td class="num ltr">{{ l.refundable }}</td>
                <td class="num"><input type="number" class="ltr qty" min="0" [max]="l.refundable" [value]="qty()[i]" (input)="setQty(i, $any($event.target).value)"
                  [disabled]="!l.refundable" /></td>
              </tr>
            }
          </tbody>
        </table>
        <mat-slide-toggle [formControl]="toCard">{{ 'pos.refundToCard' | t }}</mat-slide-toggle>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="!sale() || !any() || busy()" (click)="save()">{{ 'pos.refund' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `
    .find { display: flex; gap: 8px; align-items: baseline; }
    .qty { width: 80px; }
  `
})
class RefundDialog {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly ref = inject(MatDialogRef<RefundDialog>);
  private readonly data = inject<{ shiftId: string }>(MAT_DIALOG_DATA);
  protected readonly receipt = new FormControl('', { nonNullable: true });
  protected readonly toCard = new FormControl(false, { nonNullable: true });
  protected readonly sale = signal<Sale | null>(null);
  protected readonly qty = signal<number[]>([]);
  protected readonly any = computed(() => this.qty().some((q) => q > 0));
  protected readonly busy = signal(false);

  protected find(): void {
    const no = this.receipt.value.trim();
    if (!no) return;
    this.api.get<Sale>(`sales/pos/sales/by-receipt/${encodeURIComponent(no)}`).subscribe({
      next: (s) => {
        if (s.isRefund) return;
        this.sale.set(s);
        this.qty.set(s.lines.map(() => 0));
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected setQty(i: number, value: string): void {
    const s = this.sale();
    if (!s) return;
    const v = Math.max(0, Math.min(Number(value) || 0, s.lines[i].refundable));
    this.qty.update((q) => q.map((x, j) => (j === i ? v : x)));
  }

  protected save(): void {
    const s = this.sale();
    if (!s) return;
    this.busy.set(true);
    this.api
      .post<Sale>('sales/pos/refunds', {
        clientId: crypto.randomUUID(), shiftId: this.data.shiftId, saleId: s.id, toCard: this.toCard.value,
        lines: s.lines.map((l, i) => ({ saleLineId: l.id, quantity: this.qty()[i] })).filter((l) => l.quantity > 0)
      })
      .subscribe({
        next: (r) => this.ref.close(r),
        error: (e) => {
          this.busy.set(false);
          this.notify.error(e);
        }
      });
  }
}

@Component({
  selector: 'app-register-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ (data ? 'pos.editRegister' : 'pos.newRegister') | t }}</h2>
    <mat-dialog-content [formGroup]="form" class="form">
      <mat-form-field appearance="outline"><mat-label>{{ 'pos.code' | t }}</mat-label><input matInput class="ltr" formControlName="code" /></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>{{ 'pos.nameEn' | t }}</mat-label><input matInput formControlName="nameEn" /></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>{{ 'pos.nameAr' | t }}</mat-label><input matInput formControlName="nameAr" /></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>{{ 'pos.warehouse' | t }}</mat-label>
        <mat-select formControlName="warehouseId">@for (w of warehouses(); track w.id) { <mat-option [value]="w.id">{{ w | bi }}</mat-option> }</mat-select>
      </mat-form-field>
      <mat-form-field appearance="outline"><mat-label>{{ 'pos.cashAccount' | t }}</mat-label>
        <mat-select formControlName="cashAccountId">@for (a of accounts(); track a.id) { <mat-option [value]="a.id"><span class="ltr">{{ a.code }}</span> {{ a | bi }}</mat-option> }</mat-select>
      </mat-form-field>
      <mat-form-field appearance="outline"><mat-label>{{ 'pos.cardAccount' | t }}</mat-label>
        <mat-select formControlName="cardAccountId">@for (a of accounts(); track a.id) { <mat-option [value]="a.id"><span class="ltr">{{ a.code }}</span> {{ a | bi }}</mat-option> }</mat-select>
      </mat-form-field>
      <mat-slide-toggle formControlName="isActive">{{ 'pos.active' | t }}</mat-slide-toggle>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid" (click)="save()">{{ 'app.save' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `.form { display: grid; gap: 4px; }`
})
class RegisterDialog implements OnInit {
  private readonly api = inject(ApiService);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  protected readonly ref = inject(MatDialogRef<RegisterDialog>);
  protected readonly data = inject<Register | null>(MAT_DIALOG_DATA);
  protected readonly warehouses = signal<Master[]>([]);
  protected readonly accounts = signal<Account[]>([]);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    code: [this.data?.code ?? '', Validators.required],
    nameEn: [this.data?.nameEn ?? '', Validators.required],
    nameAr: [this.data?.nameAr ?? '', Validators.required],
    warehouseId: [this.data?.warehouseId ?? '', Validators.required],
    cashAccountId: [this.data?.cashAccountId ?? '', Validators.required],
    cardAccountId: [this.data?.cardAccountId ?? '', Validators.required],
    isActive: [this.data?.isActive ?? true]
  });

  ngOnInit(): void {
    if (this.data) this.form.controls.code.disable();
    forkJoin([this.trade.warehouses(), this.api.accounts()]).subscribe({
      next: ([w, a]) => {
        this.warehouses.set(w.filter((x) => x.isActive));
        const assets = a.filter((x) => x.isPostable && x.isActive && x.accountType === 'Asset');
        this.accounts.set(assets);
        if (!this.data) {
          this.form.patchValue({
            warehouseId: w.find((x) => x.code === 'MAIN')?.id ?? '',
            cashAccountId: assets.find((x) => x.code === '1110')?.id ?? '',
            cardAccountId: assets.find((x) => x.code === '1115')?.id ?? ''
          });
        }
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected save(): void {
    const body = this.form.getRawValue();
    (this.data ? this.api.put<string>(`sales/pos/registers/${this.data.id}`, body) : this.api.post<string>('sales/pos/registers', body)).subscribe({
      next: () => this.ref.close(true),
      error: (e) => this.notify.error(e)
    });
  }
}

/** Finds a customer (name, code or phone) for a sale on account. */
@Component({
  selector: 'app-pos-customer-pick',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'pos.onAccount' | t }}</h2>
    <mat-dialog-content>
      <mat-form-field class="full"><mat-label>{{ 'app.search' | t }}</mat-label>
        <input matInput [formControl]="search" (keydown.enter)="find()" (input)="find()" autocomplete="off" /></mat-form-field>
      @for (c of results(); track c.id) {
        <button mat-button class="full row" (click)="ref.close(c)"><span>{{ c | bi }}</span> <span class="ltr secondary">{{ c.code }} {{ c.phone }}</span></button>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end"><button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button></mat-dialog-actions>
  `,
  styles: `.full { width: 100%; } .row { justify-content: space-between; display: flex; } .secondary { color: var(--mat-sys-on-surface-variant); font-size: 12px; }`
})
class CustomerPickDialog {
  private readonly api = inject(ApiService);
  protected readonly ref = inject(MatDialogRef<CustomerPickDialog>);
  protected readonly search = new FormControl('', { nonNullable: true });
  protected readonly results = signal<{ id: string; code: string; nameEn: string; nameAr: string; phone: string | null }[]>([]);

  protected find(): void {
    const q = this.search.value.trim();
    if (q.length < 2) return;
    this.api.get<{ id: string; code: string; nameEn: string; nameAr: string; phone: string | null; isActive: boolean }[]>('sales/customers', { search: q })
      .subscribe((r) => this.results.set(r.filter((c) => c.isActive).slice(0, 15)));
  }
}

/** A manager types their PIN on the cashier's device to let a discount past the cashier limit through. Closes with the approval id. */
@Component({
  selector: 'app-pos-approval',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'pos.approvalTitle' | t }}</h2>
    <mat-dialog-content>
      <p>{{ 'pos.approvalHint' | t: { amount: data.discount.toFixed(2), max: data.max } }}</p>
      @if (loaded() && !approvers().length) {
        <p class="warn">{{ 'pos.noApprovers' | t }}</p>
      } @else {
        <mat-form-field appearance="outline" class="full"><mat-label>{{ 'pos.approver' | t }}</mat-label>
          <mat-select [formControl]="approver">
            @for (a of approvers(); track a.userId) { <mat-option [value]="a.userId">{{ a.name }}</mat-option> }
          </mat-select></mat-form-field>
        <mat-form-field appearance="outline" class="full"><mat-label>{{ 'pos.pin' | t }}</mat-label>
          <input matInput type="password" inputmode="numeric" class="ltr" autocomplete="off" maxlength="6" [formControl]="pin" (keydown.enter)="approve()" />
        </mat-form-field>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="!approver.value || pin.value.length < 4 || busy()" (click)="approve()">{{ 'pos.approve' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `.full { width: 100%; } .warn { color: var(--mat-sys-error); }`
})
class ApprovalDialog implements OnInit {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly ref = inject(MatDialogRef<ApprovalDialog>);
  protected readonly data = inject<{ shiftId: string; discount: number; max: number }>(MAT_DIALOG_DATA);
  protected readonly approvers = signal<{ userId: string; name: string }[]>([]);
  protected readonly loaded = signal(false);
  protected readonly busy = signal(false);
  protected readonly approver = new FormControl('', { nonNullable: true });
  protected readonly pin = new FormControl('', { nonNullable: true });

  ngOnInit(): void {
    this.api.get<{ userId: string; name: string }[]>('sales/pos/approvers').subscribe({
      next: (list) => {
        this.approvers.set(list);
        this.loaded.set(true);
        if (list.length === 1) this.approver.setValue(list[0].userId);
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected approve(): void {
    if (!this.approver.value || this.pin.value.length < 4 || this.busy()) return;
    this.busy.set(true);
    this.api.post<{ id: string; approverName: string }>('sales/pos/approvals', {
      shiftId: this.data.shiftId, approverUserId: this.approver.value, pin: this.pin.value, discount: this.data.discount
    }).subscribe({
      next: (a) => this.ref.close(a.id),
      error: (e) => {
        this.busy.set(false);
        this.pin.setValue('');
        this.notify.error(e);
      }
    });
  }
}

/** A manager sets the PIN they type on cashiers' devices to approve discounts. */
@Component({
  selector: 'app-pos-pin',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'pos.myPin' | t }}</h2>
    <mat-dialog-content>
      <p class="secondary">{{ 'pos.myPinHint' | t }}</p>
      <mat-form-field appearance="outline" class="full"><mat-label>{{ 'pos.pin' | t }}</mat-label>
        <input matInput type="password" inputmode="numeric" class="ltr" autocomplete="new-password" maxlength="6" [formControl]="pin" /></mat-form-field>
      <mat-form-field appearance="outline" class="full"><mat-label>{{ 'pos.pinAgain' | t }}</mat-label>
        <input matInput type="password" inputmode="numeric" class="ltr" autocomplete="new-password" maxlength="6" [formControl]="again" /></mat-form-field>
      @if (again.value && again.value !== pin.value) { <p class="warn">{{ 'pos.pinMismatch' | t }}</p> }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="pin.value.length < 4 || again.value !== pin.value" (click)="save()">{{ 'app.save' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `.full { width: 100%; } .secondary { color: var(--mat-sys-on-surface-variant); } .warn { color: var(--mat-sys-error); }`
})
class PinDialog {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly ref = inject(MatDialogRef<PinDialog>);
  protected readonly pin = new FormControl('', { nonNullable: true });
  protected readonly again = new FormControl('', { nonNullable: true });

  protected save(): void {
    this.api.put('sales/pos/my-pin', { pin: this.pin.value }).subscribe({
      next: () => this.ref.close(true),
      error: (e) => this.notify.error(e)
    });
  }
}
