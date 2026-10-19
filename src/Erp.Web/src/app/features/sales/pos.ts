import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { filter, forkJoin, switchMap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { Account } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { TradeApi } from '../../core/trade.api';
import { Master } from '../../core/trade.models';
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
}

interface CartLine {
  item: CatalogItem;
  quantity: number;
  discountPercent: number;
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
  lines: { itemId: string; quantity: number; unitPrice: number; discountPercent: number }[];
  cash: number;
  card: number;
  customerName: string | null;
  customerPhone: string | null;
  total: number;
  error?: string;
}

const CATALOG_KEY = 'erp.pos.catalog';
const QUEUE_KEY = 'erp.pos.queue';

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
const lineAmount = (l: CartLine) => round2(l.quantity * l.item.salesPrice * (1 - l.discountPercent / 100));
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
              </button>
            }
          </div>
        </section>
        <section class="cart">
          <div class="lines">
            @for (l of cart(); track l.item.id; let idx = $index) {
              <div class="line">
                <div class="what">
                  <div>{{ l.item | bi }}</div>
                  <div class="secondary ltr">{{ l.item.salesPrice | money }} × {{ l.quantity }} @if (l.discountPercent) { − {{ l.discountPercent }}% }</div>
                </div>
                <div class="qty">
                  <button mat-icon-button (click)="change(idx, -1)"><mat-icon>remove</mat-icon></button>
                  <span class="ltr">{{ l.quantity }}</span>
                  <button mat-icon-button (click)="change(idx, 1)"><mat-icon>add</mat-icon></button>
                  <button mat-icon-button [matMenuTriggerFor]="lineMenu"><mat-icon>more_vert</mat-icon></button>
                  <mat-menu #lineMenu="matMenu">
                    <button mat-menu-item (click)="discount(idx)">{{ 'pos.discount' | t }}</button>
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
                <span>{{ 'pos.lastReceipt' | t }}: <b class="ltr">{{ s.receiptNo }}</b></span>
                <button mat-button (click)="print(s)"><mat-icon>print</mat-icon> {{ 'pos.print' | t }}</button>
              </div>
            }
          </div>
        </section>
      </div>
    }
    @for (q of failed(); track q.clientId) {
      <p class="failed">{{ 'pos.syncFailed' | t }} <span class="ltr">{{ q.total | money }}</span>: {{ q.error }}
        <button mat-button (click)="discard(q)">{{ 'pos.discard' | t }}</button></p>
    }
  `,
  styles: `
    .spacer { flex: 1; }
    .secondary { color: var(--mat-sys-on-surface-variant); }
    .registers { display: grid; grid-template-columns: repeat(auto-fill, minmax(260px, 1fr)); gap: 16px; }
    .shift-bar { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin-block-end: 12px; }
    .till { display: grid; grid-template-columns: minmax(0, 3fr) minmax(300px, 2fr); gap: 16px; }
    @media (max-width: 900px) { .till { grid-template-columns: 1fr; } }
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
    .pay { display: flex; gap: 8px; }
    .pay mat-form-field { flex: 1; }
    .quick { display: flex; flex-wrap: wrap; gap: 4px; }
    .pay-btn { width: 100%; height: 56px; font-size: 1.2rem; }
    .account-btn { width: 100%; margin-top: 8px; }
    .last { display: flex; align-items: center; justify-content: space-between; margin-block-start: 8px; }
    .failed { color: var(--mat-sys-error); }
  `
})
export class PosPage implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly i18n = inject(I18nService);
  protected readonly canManage = inject(AuthService).can('sal.pos:manage');
  protected readonly registers = signal<Register[]>([]);
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
    this.api.get<Register[]>('sales/pos/registers').subscribe({ next: (r) => this.registers.set(r), error: (e) => this.notify.error(e) });
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
          this.shift.set(s);
          this.registers.update((list) => list.map((x) => (x.id === r.id ? { ...x, openShiftId: s.id } : x)));
        },
        error: (e) => this.notify.error(e)
      });
  }

  protected resume(r: Register): void {
    this.api.get<Shift[]>('sales/pos/shifts', { registerId: r.id }).subscribe({
      next: (list) => this.shift.set(list.find((s) => s.id === r.openShiftId) ?? null),
      error: (e) => this.notify.error(e)
    });
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
    if (hit) {
      this.add(hit);
      this.search.setValue('');
    } else this.notify.info(this.i18n.t('pos.notFound'));
  }

  protected add(item: CatalogItem): void {
    this.cart.update((list) => {
      const idx = list.findIndex((l) => l.item.id === item.id);
      return idx < 0 ? [...list, { item, quantity: 1, discountPercent: 0 }] : list.map((l, i) => (i === idx ? { ...l, quantity: l.quantity + 1 } : l));
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
    this.dialog.open(CustomerPickDialog, { width: '420px' }).afterClosed().pipe(filter((c): c is { id: string; nameEn: string } => !!c)).subscribe((customer) => {
      this.busy.set(true);
      this.api.post<Sale>('sales/pos/sales', {
        clientId: crypto.randomUUID(), shiftId: shift.id, soldAt: new Date().toISOString(), cash: 0, card: 0, customerId: customer.id,
        customerName: customer.nameEn, customerPhone: null,
        lines: this.cart().map((l) => ({ itemId: l.item.id, quantity: l.quantity, unitPrice: l.item.salesPrice, discountPercent: l.discountPercent }))
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
    const sale: QueuedSale = {
      clientId: crypto.randomUUID(),
      shiftId: shift.id,
      soldAt: new Date().toISOString(), // an instant (when the till rang it up), not a calendar date
      lines: this.cart().map((l) => ({ itemId: l.item.id, quantity: l.quantity, unitPrice: l.item.salesPrice, discountPercent: l.discountPercent })),
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
          this.last.set(null);
          this.reset();
          this.notify.info(this.i18n.t('pos.savedOffline'));
        } else this.notify.error(e);
      }
    });
  }

  private done(s: Sale): void {
    this.last.set(s);
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
    const next = this.queue().find((q) => !q.error);
    if (!next) return;
    this.api.post<Sale>('sales/pos/sales', next).subscribe({
      next: () => {
        this.queue.update((q) => q.filter((x) => x.clientId !== next.clientId));
        writeStore(QUEUE_KEY, this.queue());
        this.sync();
      },
      error: (e) => {
        if (isOffline(e)) return;
        this.queue.update((q) => q.map((x) => (x.clientId === next.clientId ? { ...x, error: this.notify.message(e) } : x)));
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

  protected print(s: Sale): void {
    const ar = this.i18n.isArabic();
    const rows = s.lines
      .map((l) => `<tr><td>${ar ? l.nameAr : l.nameEn}<br><small>${l.quantity} × ${l.unitPrice.toFixed(2)}${l.discountPercent ? ` −${l.discountPercent}%` : ''}</small></td>
        <td style="text-align:end">${l.amount.toFixed(2)}</td></tr>`)
      .join('');
    const w = window.open('', '_blank', 'width=360,height=600');
    if (!w) return;
    w.document.write(`<!doctype html><html dir="${ar ? 'rtl' : 'ltr'}"><head><meta charset="utf-8"><title>${s.receiptNo}</title>
      <style>body{font-family:system-ui,sans-serif;font-size:13px;width:280px;margin:8px auto}table{width:100%;border-collapse:collapse}
      td{padding:3px 0;border-bottom:1px dashed #ccc}h3,p{text-align:center;margin:4px 0}.t{font-weight:700;font-size:15px}</style></head><body>
      <h3>${this.register() ? (ar ? this.register()!.nameAr : this.register()!.nameEn) : ''}</h3>
      <p>${s.isRefund ? this.i18n.t('pos.refund') : this.i18n.t('pos.receipt')} ${s.receiptNo}</p><p>${new Date(s.soldAt).toLocaleString('en-GB')}</p>
      <table>${rows}</table>
      <table><tr class="t"><td>${this.i18n.t('app.total')}</td><td style="text-align:end">${s.total.toFixed(2)}</td></tr>
      <tr><td>${this.i18n.t('pos.cash')}</td><td style="text-align:end">${s.cashAmount.toFixed(2)}</td></tr>
      <tr><td>${this.i18n.t('pos.card')}</td><td style="text-align:end">${s.cardAmount.toFixed(2)}</td></tr>
      <tr><td>${this.i18n.t('pos.change')}</td><td style="text-align:end">${s.changeGiven.toFixed(2)}</td></tr></table>
      <p>${this.i18n.t('pos.thanks')}</p><script>window.print()</script></body></html>`);
    w.document.close();
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
