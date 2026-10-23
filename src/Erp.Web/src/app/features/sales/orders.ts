import { CURRENCIES } from '../../core/trade.models';
import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormArray, FormBuilder, FormControl, FormGroup, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { ActivatedRoute, Router } from '@angular/router';
import { filter, merge, Observable, switchMap } from 'rxjs';
import { AuthService } from '../../core/auth.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { todayIso } from '../../core/dates';
import { NotifyService } from '../../core/notify.service';
import { stockLevel } from '../../core/stock-alert';
import { SalesApi } from '../../core/sales.api';
import { Customer, DeliveryNote, Quotation, QuotationStatus, SalesInvoice, SalesOrder, SalesOrderStatus } from '../../core/sales.models';
import { TradeApi } from '../../core/trade.api';
import { Item, ItemUnit, Master } from '../../core/trade.models';
import { ConfirmDialog, ReverseDialog, ReverseResult } from '../../shared/dialogs';
import { SHARED } from '../../shared/shared';

export const QUOTE_STATUSES: QuotationStatus[] = ['Draft', 'Sent', 'Accepted', 'Rejected'];
export const SO_STATUSES: SalesOrderStatus[] = ['Draft', 'CreditHold', 'Confirmed', 'PartiallyDelivered', 'Delivered', 'Closed', 'Cancelled'];

export const quoteChip = (s: QuotationStatus) => `chip ${s === 'Accepted' ? 'ok' : s === 'Rejected' ? 'bad' : s === 'Sent' ? 'info' : 'warn'}`;
export const orderChip = (s: SalesOrderStatus) =>
  `chip ${s === 'Delivered' || s === 'Closed' ? 'ok' : s === 'CreditHold' ? 'bad' : s === 'Cancelled' ? '' : s === 'Draft' ? 'warn' : 'info'}`;

@Component({
  selector: 'app-quotations',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'quote.title' | t }}</h1>
      @if (auth.can('sal.orders:manage')) {
        <a mat-flat-button routerLink="/sales/quotations/new"><mat-icon>add</mat-icon> {{ 'quote.new' | t }}</a>
      }
    </div>
    <div class="toolbar-row">
      <mat-form-field>
        <mat-label>{{ 'app.status' | t }}</mat-label>
        <mat-select [formControl]="status">
          <mat-option value="">{{ 'po.allStatuses' | t }}</mat-option>
          @for (s of statuses; track s) { <mat-option [value]="s">{{ 'quote.status.' + s | t }}</mat-option> }
        </mat-select>
      </mat-form-field>
    </div>
    <div class="table-wrap">
      <table mat-table [dataSource]="rows()">
        <ng-container matColumnDef="no">
          <th mat-header-cell *matHeaderCellDef>{{ 'quote.no' | t }}</th>
          <td mat-cell *matCellDef="let q"><span class="ltr">{{ q.quoteNo }}</span></td>
        </ng-container>
        <ng-container matColumnDef="date">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.date' | t }}</th>
          <td mat-cell *matCellDef="let q" class="ltr">{{ q.quoteDate | d }}<div class="secondary">{{ 'quote.validUntil' | t }} {{ q.validUntil | d }}</div></td>
        </ng-container>
        <ng-container matColumnDef="customer">
          <th mat-header-cell *matHeaderCellDef>{{ 'cust.customer' | t }}</th>
          <td mat-cell *matCellDef="let q">{{ q | bi:'customerName' }}</td>
        </ng-container>
        <ng-container matColumnDef="total">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.total' | t }}</th>
          <td mat-cell *matCellDef="let q" class="num ltr">{{ q.total | money }}</td>
        </ng-container>
        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
          <td mat-cell *matCellDef="let q">
            <span [class]="chip(q.status)">{{ 'quote.status.' + q.status | t }}</span>
            @if (q.isExpired) { <span class="chip bad">{{ 'quote.expired' | t }}</span> }
          </td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns" class="clickable" (click)="router.navigate(['/sales/quotations', row.id])"></tr>
      </table>
      @if (rows().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `
})
export class QuotationsPage {
  protected readonly auth = inject(AuthService);
  protected readonly router = inject(Router);
  private readonly sales = inject(SalesApi);
  protected readonly chip = quoteChip;
  protected readonly statuses = QUOTE_STATUSES;
  protected readonly columns = ['no', 'date', 'customer', 'total', 'status'];
  protected readonly status = new FormControl('', { nonNullable: true });
  protected readonly rows = signal<Quotation[]>([]);

  constructor() {
    this.status.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => this.load());
    this.load();
  }

  private load(): void {
    this.sales.quotations(this.status.value || undefined).subscribe((r) => this.rows.set(r));
  }
}

@Component({
  selector: 'app-sales-orders',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'so.title' | t }}</h1>
      @if (auth.can('sal.orders:manage')) {
        <a mat-flat-button routerLink="/sales/orders/new"><mat-icon>add</mat-icon> {{ 'so.new' | t }}</a>
      }
    </div>
    <div class="toolbar-row">
      <mat-form-field>
        <mat-label>{{ 'app.status' | t }}</mat-label>
        <mat-select [formControl]="status">
          <mat-option value="">{{ 'po.allStatuses' | t }}</mat-option>
          @for (s of statuses; track s) { <mat-option [value]="s">{{ 'so.status.' + s | t }}</mat-option> }
        </mat-select>
      </mat-form-field>
    </div>
    <div class="table-wrap">
      <table mat-table [dataSource]="rows()">
        <ng-container matColumnDef="no">
          <th mat-header-cell *matHeaderCellDef>{{ 'so.no' | t }}</th>
          <td mat-cell *matCellDef="let o"><span class="ltr">{{ o.soNo }}</span><div class="secondary ltr">{{ o.customerRef }}</div></td>
        </ng-container>
        <ng-container matColumnDef="date">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.date' | t }}</th>
          <td mat-cell *matCellDef="let o" class="ltr">{{ o.orderDate | d }}</td>
        </ng-container>
        <ng-container matColumnDef="customer">
          <th mat-header-cell *matHeaderCellDef>{{ 'cust.customer' | t }}</th>
          <td mat-cell *matCellDef="let o">{{ o | bi:'customerName' }}</td>
        </ng-container>
        <ng-container matColumnDef="total">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.total' | t }}</th>
          <td mat-cell *matCellDef="let o" class="num ltr">{{ o.total | money }}</td>
        </ng-container>
        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
          <td mat-cell *matCellDef="let o">
            <span [class]="chip(o.status)">{{ 'so.status.' + o.status | t }}</span>
            @if (o.creditWarning) { <mat-icon class="warn-icon" [matTooltip]="o.creditWarning">warning</mat-icon> }
          </td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns" class="clickable" (click)="router.navigate(['/sales/orders', row.id])"></tr>
      </table>
      @if (rows().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `,
  styles: `.warn-icon { color: #b26a00; vertical-align: middle; font-size: 18px; width: 18px; height: 18px; }`
})
export class SalesOrdersPage {
  protected readonly auth = inject(AuthService);
  protected readonly router = inject(Router);
  private readonly sales = inject(SalesApi);
  protected readonly chip = orderChip;
  protected readonly statuses = SO_STATUSES;
  protected readonly columns = ['no', 'date', 'customer', 'total', 'status'];
  protected readonly status = new FormControl('', { nonNullable: true });
  protected readonly rows = signal<SalesOrder[]>([]);

  constructor() {
    this.status.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => this.load());
    this.load();
  }

  private load(): void {
    this.sales.orders(this.status.value || undefined).subscribe((r) => this.rows.set(r));
  }
}

type SalesLineGroup = FormGroup<{
  itemId: FormControl<string>;
  uomId: FormControl<string | null>;
  description: FormControl<string>;
  quantity: FormControl<number | null>;
  unitPrice: FormControl<number | null>;
  discountPercent: FormControl<number | null>;
}>;

/** Quotation or sales order form (route data `kind`): customer, lines with customer prices, discounts and live totals. */
@Component({
  selector: 'app-sales-doc-form',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <a mat-icon-button [routerLink]="back()"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
      <h1>{{ (kind() === 'quote' ? (id() ? 'quote.edit' : 'quote.new') : id() ? 'so.edit' : 'so.new') | t }}</h1>
    </div>
    <mat-card appearance="outlined" class="form-card">
      <form [formGroup]="form" class="form-grid">
        <mat-form-field>
          <mat-label>{{ 'cust.customer' | t }}</mat-label>
          <mat-select formControlName="customerId">
            @for (c of customers(); track c.id) { <mat-option [value]="c.id"><span class="ltr">{{ c.code }}</span> · {{ c | bi }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'app.date' | t }}</mat-label>
          <input matInput type="date" formControlName="date" />
        </mat-form-field>
        @if (kind() === 'quote') {
          <mat-form-field>
            <mat-label>{{ 'quote.validUntil' | t }}</mat-label>
            <input matInput type="date" formControlName="validUntil" />
            <mat-hint>{{ 'quote.validHint' | t }}</mat-hint>
          </mat-form-field>
        } @else {
          <mat-form-field>
            <mat-label>{{ 'po.warehouse' | t }}</mat-label>
            <mat-select formControlName="warehouseId">
              @for (w of warehouses(); track w.id) { <mat-option [value]="w.id">{{ w.code }} · {{ w | bi }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'so.expected' | t }}</mat-label>
            <input matInput type="date" formControlName="expectedDate" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'so.customerRef' | t }}</mat-label>
            <input matInput formControlName="customerRef" dir="ltr" />
          </mat-form-field>
        }
        <mat-form-field>
          <mat-label>{{ 'fx.currency' | t }}</mat-label>
          <mat-select formControlName="currency">
            @for (cur of currencies; track cur) { <mat-option [value]="cur">{{ cur }}</mat-option> }
          </mat-select>
        </mat-form-field>
        @if (form.value.currency !== 'QAR') {
          <mat-form-field>
            <mat-label>{{ 'fx.rate' | t: { c: form.value.currency ?? '' } }}</mat-label>
            <input matInput type="number" min="0" step="0.0001" formControlName="exchangeRate" dir="ltr" (change)="repriceAll()" />
          </mat-form-field>
        }
        <mat-form-field class="full">
          <mat-label>{{ 'app.notes' | t }}</mat-label>
          <input matInput formControlName="notes" />
        </mat-form-field>
      </form>
      @if (selectedCustomer(); as c) {
        <div class="secondary customer-hint">
          {{ 'cust.creditLimit' | t }}: <span class="ltr">{{ c.creditLimit === null ? ('cust.noLimit' | t) : (c.creditLimit | money) }}</span>
          · {{ 'sup.terms' | t }}: <span class="ltr">{{ c.paymentTermsDays }}</span> {{ 'app.days' | t }}
        </div>
      }

      <table class="line-editor">
        <thead>
          <tr><th class="item-col">{{ 'po.item' | t }}</th><th>{{ 'po.description' | t }}</th><th class="narrow">{{ 'units.unit' | t }}</th>
            <th class="narrow">{{ 'po.qty' | t }}</th><th class="narrow">{{ 'po.price' | t }}</th>
            <th class="narrow">{{ 'so.discount' | t }}</th><th class="narrow num">{{ 'po.amount' | t }}</th><th></th></tr>
        </thead>
        <tbody>
          @for (line of lines.controls; track line; let i = $index) {
            <tr [formGroup]="line">
              <td>
                <mat-form-field>
                  <mat-select formControlName="itemId" (selectionChange)="itemChanged(line)">
                    @for (it of items(); track it.id) { <mat-option [value]="it.id"><span class="ltr">{{ it.sku }}</span> · {{ it | bi }}</mat-option> }
                  </mat-select>
                </mat-form-field>
                @if (itemOf(line); as it) {
                  @if (it.itemType === 'Stock') {
                    <div class="secondary" [class]="'stock-' + level(line, it)">
                      {{ 'so.onHand' | t }}: <span class="ltr">{{ it.quantityOnHand }} {{ it.uomCode }}</span></div>
                  }
                }
              </td>
              <td><mat-form-field><input matInput formControlName="description" /></mat-form-field></td>
              <td class="narrow"><mat-form-field><mat-select formControlName="uomId" (selectionChange)="priceLine(line)">
                @for (u of unitsOf(line.controls.itemId.value); track u.uomId) { <mat-option [value]="u.isBase ? null : u.uomId">{{ u.uomCode }}</mat-option> }
              </mat-select></mat-form-field></td>
              <td class="narrow"><mat-form-field><input matInput type="number" min="0" formControlName="quantity" dir="ltr" /></mat-form-field></td>
              <td class="narrow"><mat-form-field><input matInput type="number" min="0" formControlName="unitPrice" dir="ltr" /></mat-form-field></td>
              <td class="narrow"><mat-form-field><input matInput type="number" min="0" max="100" formControlName="discountPercent" dir="ltr" /></mat-form-field></td>
              <td class="num ltr">{{ amount(line) | money }}</td>
              <td><button mat-icon-button type="button" (click)="lines.removeAt(i)" [disabled]="lines.length === 1"><mat-icon>delete</mat-icon></button></td>
            </tr>
          }
          <tr class="total-row"><td colspan="6">{{ 'po.total' | t }}</td><td class="num ltr">{{ total() | money }}</td><td></td></tr>
        </tbody>
      </table>
      <div class="actions bar">
        <button mat-button type="button" (click)="addLine()"><mat-icon>add</mat-icon> {{ 'doc.addLine' | t }}</button>
        <span class="spacer"></span>
        <button mat-stroked-button (click)="save(false)" [disabled]="invalid() || busy()">{{ 'po.saveDraft' | t }}</button>
        <button mat-flat-button (click)="save(true)" [disabled]="invalid() || busy()">
          <mat-icon>{{ kind() === 'quote' ? 'send' : 'check_circle' }}</mat-icon> {{ (kind() === 'quote' ? 'quote.saveSend' : 'so.saveConfirm') | t }}</button>
      </div>
    </mat-card>
  `,
  styles: `
    .bar { align-items: center; margin-top: 8px; }
    .spacer { flex: 1; }
    .customer-hint { margin-block: 4px 12px; }
    .short { color: #b3261e; }
  `
})
export class SalesDocFormPage implements OnInit {
  readonly id = input<string>();
  readonly kind = input<'quote' | 'order'>('order');
  private readonly sales = inject(SalesApi);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly fb = inject(FormBuilder).nonNullable;

  protected readonly customers = signal<Customer[]>([]);
  protected readonly warehouses = signal<Master[]>([]);
  protected readonly items = signal<Item[]>([]);
  protected readonly busy = signal(false);
  protected readonly form = this.fb.group({
    customerId: ['', Validators.required],
    date: [todayIso(), Validators.required],
    validUntil: [''],
    warehouseId: [''],
    expectedDate: [''],
    customerRef: [''],
    notes: [''],
    currency: ['QAR'],
    exchangeRate: [null as number | null]
  });
  protected readonly currencies = CURRENCIES;
  protected readonly lines = new FormArray<SalesLineGroup>([]);
  private readonly itemUnits = signal(new Map<string, ItemUnit[]>());
  private readonly version = signal(0);
  private readonly customerId = signal('');
  protected readonly selectedCustomer = computed(() => this.customers().find((c) => c.id === this.customerId()) ?? null);
  protected readonly total = computed(() => {
    this.version();
    return this.lines.controls.reduce((s, l) => s + this.amount(l), 0);
  });
  protected readonly back = computed(() => {
    const base = this.kind() === 'quote' ? '/sales/quotations' : '/sales/orders';
    return this.id() ? [base, this.id()] : [base];
  });

  constructor() {
    merge(this.lines.valueChanges).pipe(takeUntilDestroyed()).subscribe(() => this.version.update((v) => v + 1));
    this.form.controls.customerId.valueChanges.pipe(takeUntilDestroyed()).subscribe((id) => {
      this.customerId.set(id);
      const customer = this.customers().find((c) => c.id === id);
      if (customer && !this.id()) this.form.controls.currency.setValue(customer.currency ?? 'QAR');
      this.repriceAll();
    });
  }

  ngOnInit(): void {
    if (this.kind() === 'order') {
      this.form.controls.warehouseId.addValidators(Validators.required);
      this.trade.warehouses().subscribe((w) => {
        this.warehouses.set(w.filter((x) => x.isActive));
        if (!this.form.value.warehouseId) this.form.patchValue({ warehouseId: w.find((x) => x.isActive)?.id ?? '' });
      });
    }
    this.trade.items({ active: true, pageSize: 500 }).subscribe((r) => this.items.set(r.items));
    this.sales.customers().subscribe((c) => {
      this.customers.set(c.filter((x) => x.isActive));
      const preset = this.route.snapshot.queryParamMap.get('customerId');
      if (preset && !this.id()) this.form.controls.customerId.setValue(preset);
    });
    const id = this.id();
    if (!id) return this.addLine();
    const load: Observable<Quotation | SalesOrder> = this.kind() === 'quote' ? this.sales.quotation(id) : this.sales.order(id);
    load.subscribe((d) => {
      for (const l of d.lines) {
        this.loadUnits(l.itemId);
        this.addLine(l.itemId, l.description ?? '', l.uomQuantity, l.uomPrice, l.discountPercent, l.uomId);
      }
      if ('quoteNo' in d) this.form.patchValue({ customerId: d.customerId, date: d.quoteDate, validUntil: d.validUntil, notes: d.notes ?? '' }, { emitEvent: false });
      else
        this.form.patchValue({ customerId: d.customerId, date: d.orderDate, warehouseId: d.warehouseId, expectedDate: d.expectedDate ?? '',
          customerRef: d.customerRef ?? '', notes: d.notes ?? '' }, { emitEvent: false });
      this.customerId.set(d.customerId);
    });
  }

  protected itemOf(line: SalesLineGroup): Item | undefined {
    return this.items().find((i) => i.id === line.controls.itemId.value);
  }

  /** Stock against this line, in base units: out (not enough), low (at or below the reorder level after it) or ok. */
  protected level(line: SalesLineGroup, it: Item): string {
    return stockLevel(it.quantityOnHand, it.reorderLevel, (Number(line.controls.quantity.value) || 0) * this.factor(line));
  }

  protected amount(line: SalesLineGroup): number {
    const v = line.getRawValue();
    return Math.round((Number(v.quantity) || 0) * (Number(v.unitPrice) || 0) * (100 - (Number(v.discountPercent) || 0))) / 100;
  }

  protected invalid(): boolean {
    return this.form.invalid || this.lines.invalid || this.lines.length === 0;
  }

  protected unitsOf(itemId: string): ItemUnit[] {
    return this.itemUnits().get(itemId) ?? [];
  }

  protected itemChanged(line: SalesLineGroup): void {
    line.controls.uomId.setValue(null);
    this.loadUnits(line.controls.itemId.value);
    this.priceLine(line);
  }

  private loadUnits(itemId: string): void {
    if (!itemId || this.itemUnits().has(itemId)) return;
    this.trade.units(itemId).subscribe((u) => this.itemUnits.update((m) => new Map(m).set(itemId, u)));
  }

  /** Base units per the line's unit. */
  private factor(line: SalesLineGroup): number {
    return this.unitsOf(line.controls.itemId.value).find((u) => !u.isBase && u.uomId === line.controls.uomId.value)?.factor ?? 1;
  }

  protected addLine(itemId = '', description = '', quantity: number | null = 1, unitPrice: number | null = null, discount: number | null = 0,
    uomId: string | null = null): void {
    this.lines.push(
      this.fb.group({
        itemId: this.fb.control(itemId, Validators.required),
        uomId: this.fb.control<string | null>(uomId),
        description: this.fb.control(description),
        quantity: this.fb.control<number | null>(quantity, [Validators.required, Validators.min(0.000001)]),
        unitPrice: this.fb.control<number | null>(unitPrice, [Validators.required, Validators.min(0)]),
        discountPercent: this.fb.control<number | null>(discount, [Validators.min(0), Validators.max(100)])
      }) as SalesLineGroup
    );
  }

  /** The customer's price (price list, then the item's sales price) for a newly chosen item. */
  protected priceLine(line: SalesLineGroup): void {
    const itemId = line.controls.itemId.value;
    if (!itemId) return;
    const rate = this.form.value.currency !== 'QAR' ? Number(this.form.value.exchangeRate) || null : null;
    this.sales.prices(this.customerId() || null, [itemId], rate).subscribe(([p]) => {
      if (p?.unitPrice !== null && p?.unitPrice !== undefined) line.controls.unitPrice.setValue(Math.round(p.unitPrice * this.factor(line) * 1000000) / 1000000);
    });
  }

  protected save(andNext: boolean): void {
    const v = this.form.getRawValue();
    const lines = this.lines.getRawValue().map((l) => ({
      itemId: l.itemId, description: l.description || null, quantity: Number(l.quantity), unitPrice: Number(l.unitPrice),
      discountPercent: Number(l.discountPercent) || 0, uomId: l.uomId
    }));
    const quote = this.kind() === 'quote';
    const fx = { currency: v.currency, exchangeRate: v.currency === 'QAR' ? null : Number(v.exchangeRate) };
    const body = quote
      ? { customerId: v.customerId, quoteDate: v.date, validUntil: v.validUntil || null, notes: v.notes || null, lines, ...fx }
      : { customerId: v.customerId, orderDate: v.date, expectedDate: v.expectedDate || null, warehouseId: v.warehouseId, customerRef: v.customerRef || null,
          notes: v.notes || null, lines, ...fx };
    this.busy.set(true);
    const saved: Observable<{ id: string }> = quote
      ? this.sales.saveQuotation(this.id() ?? null, body).pipe(switchMap((q) => (andNext && q.status === 'Draft' ? this.sales.quotationAction(q.id, 'send') : [q])))
      : this.sales.saveOrder(this.id() ?? null, body).pipe(switchMap((o) => (andNext ? this.sales.orderAction(o.id, 'confirm') : [o])));
    saved.subscribe({
      next: (d) => {
        this.notify.success();
        void this.router.navigate([quote ? '/sales/quotations' : '/sales/orders', d.id]);
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }

  private repriceAll(): void {
    const ids = [...new Set(this.lines.controls.map((l) => l.controls.itemId.value).filter(Boolean))];
    if (!ids.length || !this.customerId()) return;
    this.sales.prices(this.customerId(), ids).subscribe((prices) => {
      for (const l of this.lines.controls) {
        const p = prices.find((x) => x.itemId === l.controls.itemId.value);
        if (p?.unitPrice !== null && p?.unitPrice !== undefined) l.controls.unitPrice.setValue(Math.round(p.unitPrice * this.factor(l) * 1000000) / 1000000);
      }
    });
  }
}

/** The warehouse a converted quotation's order takes stock from. */
@Component({
  selector: 'app-convert-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'quote.convert' | t }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field>
          <mat-label>{{ 'po.warehouse' | t }}</mat-label>
          <mat-select formControlName="warehouseId">
            @for (w of warehouses(); track w.id) { <mat-option [value]="w.id">{{ w.code }} · {{ w | bi }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'app.date' | t }}</mat-label>
          <input matInput type="date" formControlName="orderDate" />
        </mat-form-field>
        <mat-form-field class="full">
          <mat-label>{{ 'so.customerRef' | t }}</mat-label>
          <input matInput formControlName="customerRef" dir="ltr" />
        </mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid" (click)="ref.close(form.getRawValue())">{{ 'quote.convert' | t }}</button>
    </mat-dialog-actions>
  `
})
export class ConvertDialog {
  protected readonly ref = inject(MatDialogRef<ConvertDialog>);
  protected readonly warehouses = signal<Master[]>([]);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    warehouseId: ['', Validators.required],
    orderDate: [todayIso(), Validators.required],
    customerRef: ['']
  });

  constructor() {
    inject(TradeApi)
      .warehouses()
      .subscribe((w) => {
        this.warehouses.set(w.filter((x) => x.isActive));
        this.form.patchValue({ warehouseId: w.find((x) => x.isActive)?.id ?? '' });
      });
  }
}

@Component({
  selector: 'app-quotation-detail',
  imports: [...SHARED],
  template: `
    @if (quote(); as q) {
      <div class="page-header">
        <a mat-icon-button routerLink="/sales/quotations"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
        <h1>{{ 'quote.no' | t }} <span class="ltr">{{ q.quoteNo }}</span></h1>
        <span [class]="chip(q.status)">{{ 'quote.status.' + q.status | t }}</span>
        @if (q.isExpired) { <span class="chip bad">{{ 'quote.expired' | t }}</span> }
      </div>
      <div class="actions header-actions">
        <button mat-stroked-button (click)="pdf()"><mat-icon>picture_as_pdf</mat-icon> PDF</button>
        @if ((q.status === 'Draft' || q.status === 'Sent') && auth.can('sal.orders:manage')) {
          <a mat-stroked-button [routerLink]="['/sales/quotations', q.id, 'edit']"><mat-icon>edit</mat-icon> {{ 'app.edit' | t }}</a>
          @if (q.status === 'Draft') {
            <button mat-stroked-button (click)="act('send')" [disabled]="busy()"><mat-icon>send</mat-icon> {{ 'quote.markSent' | t }}</button>
          }
          <button mat-flat-button (click)="convert()" [disabled]="busy()"><mat-icon>shopping_bag</mat-icon> {{ 'quote.convert' | t }}</button>
          <button mat-button color="warn" (click)="act('reject')" [disabled]="busy()">{{ 'quote.reject' | t }}</button>
        }
        @if (q.status === 'Draft' && auth.can('sal.orders:manage')) {
          <button mat-button color="warn" (click)="remove()" [disabled]="busy()"><mat-icon>delete</mat-icon> {{ 'app.delete' | t }}</button>
        }
        @if (q.salesOrderId) {
          <a mat-stroked-button [routerLink]="['/sales/orders', q.salesOrderId]"><mat-icon>shopping_bag</mat-icon> {{ 'quote.viewOrder' | t }}</a>
        }
      </div>
      @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
      <div class="facts">
        <span>{{ 'cust.customer' | t }}: <a [routerLink]="['/sales/customers', q.customerId]"><b>{{ q | bi:'customerName' }}</b></a></span>
        <span>{{ 'app.date' | t }}: <b class="ltr">{{ q.quoteDate | d }}</b></span>
        <span>{{ 'quote.validUntil' | t }}: <b class="ltr">{{ q.validUntil | d }}</b></span>
        <span>{{ 'po.total' | t }}: <b class="ltr">{{ q.total | money }}</b></span>
      </div>
      @if (q.notes) { <p class="secondary">{{ q.notes }}</p> }
      <div class="table-wrap">
        <table mat-table [dataSource]="q.lines">
          <ng-container matColumnDef="item">
            <th mat-header-cell *matHeaderCellDef>{{ 'po.item' | t }}</th>
            <td mat-cell *matCellDef="let l"><span class="ltr">{{ l.sku }}</span> · {{ l | bi }} @if (l.description) { <div class="secondary">{{ l.description }}</div> }</td>
          </ng-container>
          <ng-container matColumnDef="qty">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.qty' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr">
              @if (l.uomId) { {{ l.uomQuantity }} <span class="secondary">{{ l.orderUomCode }}</span><div class="secondary">= {{ l.quantity }} {{ l.uomCode }}</div> }
              @else { {{ l.quantity }} <span class="secondary">{{ l.uomCode }}</span> }</td>
          </ng-container>
          <ng-container matColumnDef="price">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.price' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr">{{ l.uomPrice | money }}@if (l.uomId) { <span class="secondary"> / {{ l.orderUomCode }}</span> }</td>
          </ng-container>
          <ng-container matColumnDef="discount">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'so.discount' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr">{{ l.discountPercent ? l.discountPercent + '%' : '' }}</td>
          </ng-container>
          <ng-container matColumnDef="amount">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.amount' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr">{{ l.amount | money }}</td>
          </ng-container>
          <tr mat-header-row *matHeaderRowDef="columns"></tr>
          <tr mat-row *matRowDef="let row; columns: columns"></tr>
        </table>
      </div>
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: `.header-actions { margin-block: -8px 16px; }`
})
export class QuotationDetailPage implements OnInit {
  readonly id = input.required<string>();
  protected readonly auth = inject(AuthService);
  private readonly sales = inject(SalesApi);
  private readonly notify = inject(NotifyService);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  protected readonly chip = quoteChip;
  protected readonly columns = ['item', 'qty', 'price', 'discount', 'amount'];
  protected readonly quote = signal<Quotation | null>(null);
  protected readonly busy = signal(false);

  ngOnInit(): void {
    this.sales.quotation(this.id()).subscribe({ next: (q) => this.quote.set(q), error: (e) => this.notify.error(e) });
  }

  protected pdf(): void {
    this.sales.quotationPdf(this.id()).subscribe({ error: (e) => this.notify.error(e) });
  }

  protected act(action: 'send' | 'reject'): void {
    this.busy.set(true);
    this.sales.quotationAction(this.id(), action).subscribe({
      next: (q) => {
        this.busy.set(false);
        this.quote.set(q);
        this.notify.success();
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }

  protected convert(): void {
    this.dialog
      .open(ConvertDialog)
      .afterClosed()
      .pipe(filter(Boolean), switchMap((body) => this.sales.convertQuotation(this.id(), body)))
      .subscribe({
        next: (o) => {
          this.notify.success();
          void this.router.navigate(['/sales/orders', o.id]);
        },
        error: (e) => this.notify.error(e)
      });
  }

  protected remove(): void {
    this.dialog
      .open(ConfirmDialog, { data: { title: 'app.delete', message: 'app.delete' } })
      .afterClosed()
      .pipe(filter(Boolean), switchMap(() => this.sales.deleteQuotation(this.id())))
      .subscribe({ next: () => void this.router.navigate(['/sales/quotations']), error: (e) => this.notify.error(e) });
  }
}

/** Quantities delivered now, per open stock line of the order. */
@Component({
  selector: 'app-deliver-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'dn.new' | t }} · <span class="ltr">{{ data.order.soNo }}</span></h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field>
          <mat-label>{{ 'app.date' | t }}</mat-label>
          <input matInput type="date" formControlName="deliveryDate" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'dn.vehicle' | t }}</mat-label>
          <input matInput formControlName="vehicleNo" dir="ltr" />
        </mat-form-field>
        <mat-form-field class="full">
          <mat-label>{{ 'dn.address' | t }}</mat-label>
          <input matInput formControlName="deliveryAddress" />
        </mat-form-field>
      </form>
      <table class="line-editor">
        <thead><tr><th>{{ 'po.item' | t }}</th><th class="num">{{ 'po.qty' | t }}</th><th class="num">{{ 'dn.remaining' | t }}</th><th class="narrow">{{ 'dn.now' | t }}</th></tr></thead>
        <tbody>
          @for (l of open; track l.id) {
            <tr>
              <td><span class="ltr">{{ l.sku }}</span> · {{ l | bi }}</td>
              <td class="num ltr">{{ l.quantity }}</td>
              <td class="num ltr">{{ l.quantity - l.quantityDelivered }}</td>
              <td class="narrow"><mat-form-field><input matInput type="number" min="0" [max]="l.quantity - l.quantityDelivered" [formControl]="qty[l.id]" dir="ltr" /></mat-form-field></td>
            </tr>
          }
        </tbody>
      </table>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="busy() || !anything()" (click)="save()">{{ 'doc.post' | t }}</button>
    </mat-dialog-actions>
  `
})
export class DeliverDialog {
  protected readonly data = inject<{ order: SalesOrder }>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<DeliverDialog, DeliveryNote>);
  private readonly sales = inject(SalesApi);
  private readonly notify = inject(NotifyService);
  protected readonly busy = signal(false);
  protected readonly open = this.data.order.lines.filter((l) => l.itemType === 'Stock' && l.quantityDelivered < l.quantity);
  protected readonly qty: Record<string, FormControl<number | null>> = Object.fromEntries(
    this.open.map((l) => [l.id, new FormControl<number | null>(l.quantity - l.quantityDelivered)])
  );
  protected readonly form = inject(FormBuilder).nonNullable.group({
    deliveryDate: [todayIso(), Validators.required],
    vehicleNo: [''],
    deliveryAddress: ['']
  });

  protected anything(): boolean {
    return Object.values(this.qty).some((c) => Number(c.value) > 0);
  }

  protected save(): void {
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.sales
      .createDelivery({
        salesOrderId: this.data.order.id,
        deliveryDate: v.deliveryDate,
        vehicleNo: v.vehicleNo || null,
        deliveryAddress: v.deliveryAddress || null,
        lines: this.open.filter((l) => Number(this.qty[l.id].value) > 0).map((l) => ({ salesOrderLineId: l.id, quantity: Number(this.qty[l.id].value) }))
      })
      .subscribe({
        next: (d) => {
          this.notify.success();
          this.ref.close(d);
        },
        error: (e) => {
          this.busy.set(false);
          this.notify.error(e);
        }
      });
  }
}

@Component({
  selector: 'app-sales-order-detail',
  imports: [...SHARED],
  template: `
    @if (order(); as o) {
      <div class="page-header">
        <a mat-icon-button routerLink="/sales/orders"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
        <h1>{{ 'so.no' | t }} <span class="ltr">{{ o.soNo }}</span></h1>
        <span [class]="chip(o.status)">{{ 'so.status.' + o.status | t }}</span>
      </div>
      <div class="actions header-actions">
        @if (o.status === 'Draft' && auth.can('sal.orders:manage')) {
          <a mat-stroked-button [routerLink]="['/sales/orders', o.id, 'edit']"><mat-icon>edit</mat-icon> {{ 'app.edit' | t }}</a>
          <button mat-flat-button (click)="act('confirm')" [disabled]="busy()"><mat-icon>check_circle</mat-icon> {{ 'so.confirm' | t }}</button>
        }
        @if (o.status === 'CreditHold' && auth.can('sal.credit:approve')) {
          <button mat-flat-button (click)="act('approve-credit')" [disabled]="busy()"><mat-icon>verified</mat-icon> {{ 'so.approveCredit' | t }}</button>
          <button mat-stroked-button (click)="act('reject')" [disabled]="busy()">{{ 'so.reject' | t }}</button>
        }
        @if ((o.status === 'Confirmed' || o.status === 'PartiallyDelivered') && hasOpenStock() && auth.can('sal.deliveries:post')) {
          <button mat-flat-button (click)="deliver(o)" [disabled]="busy()"><mat-icon>local_shipping</mat-icon> {{ 'so.deliver' | t }}</button>
        }
        @if (hasOpenServices() && auth.can('sal.invoices:post')) {
          <a mat-stroked-button routerLink="/sales/invoices/new" [queryParams]="{ customerId: o.customerId, orderId: o.id }">
            <mat-icon>receipt</mat-icon> {{ 'so.invoiceServices' | t }}</a>
        }
        @if (['Confirmed', 'PartiallyDelivered', 'Delivered'].includes(o.status) && auth.can('sal.orders:manage')) {
          <button mat-button (click)="act('close', true)" [disabled]="busy()">{{ 'po.close' | t }}</button>
        }
        @if (['Draft', 'CreditHold', 'Confirmed'].includes(o.status) && auth.can('sal.orders:manage')) {
          <button mat-button color="warn" (click)="act('cancel', true)" [disabled]="busy()">{{ 'po.cancel' | t }}</button>
        }
      </div>
      @if (o.status === 'CreditHold') { <div class="error-panel">{{ 'so.holdHint' | t }}</div> }
      @if (o.creditWarning && o.status !== 'CreditHold') { <div class="error-panel warn">{{ o.creditWarning }}</div> }
      @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
      <div class="facts">
        <span>{{ 'cust.customer' | t }}: <a [routerLink]="['/sales/customers', o.customerId]"><b>{{ o | bi:'customerName' }}</b></a></span>
        <span>{{ 'app.date' | t }}: <b class="ltr">{{ o.orderDate | d }}</b></span>
        @if (o.expectedDate) { <span>{{ 'so.expected' | t }}: <b class="ltr">{{ o.expectedDate | d }}</b></span> }
        @if (o.customerRef) { <span>{{ 'so.customerRef' | t }}: <b class="ltr">{{ o.customerRef }}</b></span> }
        <span>{{ 'po.warehouse' | t }}: <b>{{ warehouseCode(o.warehouseId) }}</b></span>
        <span>{{ 'po.total' | t }}: <b class="ltr">{{ o.total | money }}</b></span>
        @if (o.quotationId) { <a [routerLink]="['/sales/quotations', o.quotationId]">{{ 'so.fromQuote' | t }}</a> }
      </div>
      @if (o.notes) { <p class="secondary">{{ o.notes }}</p> }
      <div class="table-wrap">
        <table mat-table [dataSource]="o.lines">
          <ng-container matColumnDef="item">
            <th mat-header-cell *matHeaderCellDef>{{ 'po.item' | t }}</th>
            <td mat-cell *matCellDef="let l">
              <span class="ltr">{{ l.sku }}</span> · {{ l | bi }}
              @if (l.itemType === 'Service') { <span class="chip info">{{ 'item.type.Service' | t }}</span> }
              @if (l.description) { <div class="secondary">{{ l.description }}</div> }
            </td>
          </ng-container>
          <ng-container matColumnDef="qty">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.qty' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr">
              @if (l.uomId) { {{ l.uomQuantity }} <span class="secondary">{{ l.orderUomCode }}</span><div class="secondary">= {{ l.quantity }} {{ l.uomCode }}</div> }
              @else { {{ l.quantity }} <span class="secondary">{{ l.uomCode }}</span> }</td>
          </ng-container>
          <ng-container matColumnDef="price">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.price' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr">{{ l.uomPrice | money }}@if (l.uomId) { <span class="secondary"> / {{ l.orderUomCode }}</span> }
              @if (l.discountPercent) { <div class="secondary">−{{ l.discountPercent }}%</div> }</td>
          </ng-container>
          <ng-container matColumnDef="amount">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.amount' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr">{{ l.amount | money }}</td>
          </ng-container>
          <ng-container matColumnDef="delivered">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'so.delivered' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr">{{ l.itemType === 'Stock' ? l.quantityDelivered : '—' }}</td>
          </ng-container>
          <ng-container matColumnDef="invoiced">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.invoiced' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr">{{ l.quantityInvoiced }}</td>
          </ng-container>
          <tr mat-header-row *matHeaderRowDef="columns"></tr>
          <tr mat-row *matRowDef="let row; columns: columns"></tr>
        </table>
      </div>

      @if (deliveries().length) {
        <div class="section-title">{{ 'dn.title' | t }}</div>
        <div class="table-wrap">
          <table mat-table [dataSource]="deliveries()">
            <ng-container matColumnDef="no">
              <th mat-header-cell *matHeaderCellDef>{{ 'dn.no' | t }}</th>
              <td mat-cell *matCellDef="let d"><span class="ltr">{{ d.dnNo }}</span> <span class="secondary ltr">{{ d.deliveryDate | d }}</span></td>
            </ng-container>
            <ng-container matColumnDef="lines">
              <th mat-header-cell *matHeaderCellDef>{{ 'doc.lines' | t }}</th>
              <td mat-cell *matCellDef="let d">
                @for (l of d.lines; track l.id) { <div><span class="ltr">{{ l.sku }}</span> × <span class="ltr">{{ l.quantity }}</span>
                  @if (l.quantityInvoiced < l.quantity) { <span class="chip warn">{{ 'dn.notInvoiced' | t }}</span> }</div> }
              </td>
            </ng-container>
            <ng-container matColumnDef="cost">
              <th mat-header-cell *matHeaderCellDef class="num">{{ 'dn.cost' | t }}</th>
              <td mat-cell *matCellDef="let d" class="num ltr">{{ d.totalCost | money }}</td>
            </ng-container>
            <ng-container matColumnDef="status">
              <th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
              <td mat-cell *matCellDef="let d"><span class="chip" [class.ok]="d.status === 'Posted'">{{ 'doc.status.' + d.status | t }}</span></td>
            </ng-container>
            <ng-container matColumnDef="actions">
              <th mat-header-cell *matHeaderCellDef></th>
              <td mat-cell *matCellDef="let d">
                <button mat-icon-button (click)="deliveryPdf(d)" [matTooltip]="'dn.print' | t"><mat-icon>print</mat-icon></button>
                @if (d.status === 'Posted' && hasUninvoiced(d) && auth.can('sal.invoices:post')) {
                  <button mat-icon-button (click)="invoice(d)" [matTooltip]="'dn.invoice' | t"><mat-icon>receipt</mat-icon></button>
                }
                @if (d.status === 'Posted' && auth.can('sal.deliveries:post')) {
                  <button mat-icon-button (click)="reverseDelivery(d)" [matTooltip]="'doc.reverse' | t"><mat-icon>undo</mat-icon></button>
                }
              </td>
            </ng-container>
            <tr mat-header-row *matHeaderRowDef="deliveryColumns"></tr>
            <tr mat-row *matRowDef="let row; columns: deliveryColumns"></tr>
          </table>
        </div>
      }
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: `
    .header-actions { margin-block: -8px 16px; }
    .error-panel.warn { background: #fff4e5; color: #8a4b00; }
  `
})
export class SalesOrderDetailPage implements OnInit {
  readonly id = input.required<string>();
  protected readonly auth = inject(AuthService);
  private readonly sales = inject(SalesApi);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  private readonly i18n = inject(I18nService);
  protected readonly chip = orderChip;
  protected readonly columns = ['item', 'qty', 'price', 'amount', 'delivered', 'invoiced'];
  protected readonly deliveryColumns = ['no', 'lines', 'cost', 'status', 'actions'];
  protected readonly order = signal<SalesOrder | null>(null);
  protected readonly deliveries = signal<DeliveryNote[]>([]);
  protected readonly warehouses = signal<Master[]>([]);
  protected readonly busy = signal(false);
  protected readonly hasOpenStock = computed(() => !!this.order()?.lines.some((l) => l.itemType === 'Stock' && l.quantityDelivered < l.quantity));
  protected readonly hasOpenServices = computed(() => {
    const o = this.order();
    return !!o && ['Confirmed', 'PartiallyDelivered', 'Delivered'].includes(o.status) &&
      o.lines.some((l) => l.itemType === 'Service' && l.quantityInvoiced < l.quantity);
  });

  ngOnInit(): void {
    this.trade.warehouses().subscribe((w) => this.warehouses.set(w));
    this.load();
  }

  protected warehouseCode(id: string): string {
    return this.warehouses().find((x) => x.id === id)?.code ?? '';
  }

  protected hasUninvoiced(d: DeliveryNote): boolean {
    return d.lines.some((l) => l.quantityInvoiced < l.quantity);
  }

  protected act(action: 'confirm' | 'approve-credit' | 'reject' | 'cancel' | 'close', confirm = false): void {
    const run = () => {
      this.busy.set(true);
      this.sales.orderAction(this.id(), action).subscribe({
        next: (o) => {
          this.busy.set(false);
          this.order.set(o);
          if (o.status === 'CreditHold') this.notify.info(this.i18n.t('so.holdHint'));
          else this.notify.success();
        },
        error: (e) => {
          this.busy.set(false);
          this.notify.error(e);
        }
      });
    };
    if (!confirm) return run();
    this.dialog
      .open(ConfirmDialog, { data: { title: `po.${action}`, message: `po.${action}` } })
      .afterClosed()
      .pipe(filter(Boolean))
      .subscribe(run);
  }

  protected deliver(o: SalesOrder): void {
    this.dialog
      .open(DeliverDialog, { data: { order: o }, width: '760px' })
      .afterClosed()
      .subscribe((d?: DeliveryNote) => d && this.load());
  }

  protected deliveryPdf(d: DeliveryNote): void {
    this.sales.deliveryPdf(d.id).subscribe({ error: (e) => this.notify.error(e) });
  }

  protected invoice(d: DeliveryNote): void {
    this.sales.invoiceDelivery(d.id, todayIso()).subscribe({
      next: (i: SalesInvoice) => {
        this.notify.success();
        void this.router.navigate(['/sales/invoices', i.id]);
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected reverseDelivery(d: DeliveryNote): void {
    this.dialog
      .open(ReverseDialog, { data: {} })
      .afterClosed()
      .pipe(
        filter((x): x is ReverseResult => !!x),
        switchMap((x) => this.sales.reverseDelivery(d.id, x.date, x.reason))
      )
      .subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  private load(): void {
    this.sales.order(this.id()).subscribe({ next: (o) => this.order.set(o), error: (e) => this.notify.error(e) });
    this.sales.deliveries({ salesOrderId: this.id() }).subscribe((d) => this.deliveries.set(d));
  }
}

@Component({
  selector: 'app-deliveries',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'dn.title' | t }}</h1></div>
    <div class="table-wrap">
      <table mat-table [dataSource]="rows()">
        <ng-container matColumnDef="no">
          <th mat-header-cell *matHeaderCellDef>{{ 'dn.no' | t }}</th>
          <td mat-cell *matCellDef="let d"><span class="ltr">{{ d.dnNo }}</span></td>
        </ng-container>
        <ng-container matColumnDef="date">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.date' | t }}</th>
          <td mat-cell *matCellDef="let d" class="ltr">{{ d.deliveryDate | d }}</td>
        </ng-container>
        <ng-container matColumnDef="customer">
          <th mat-header-cell *matHeaderCellDef>{{ 'cust.customer' | t }}</th>
          <td mat-cell *matCellDef="let d">{{ d | bi:'customerName' }}</td>
        </ng-container>
        <ng-container matColumnDef="order">
          <th mat-header-cell *matHeaderCellDef>{{ 'so.no' | t }}</th>
          <td mat-cell *matCellDef="let d"><a class="ltr" [routerLink]="['/sales/orders', d.salesOrderId]">{{ d.soNo }}</a></td>
        </ng-container>
        <ng-container matColumnDef="lines">
          <th mat-header-cell *matHeaderCellDef>{{ 'doc.lines' | t }}</th>
          <td mat-cell *matCellDef="let d">@for (l of d.lines; track l.id) { <div><span class="ltr">{{ l.sku }}</span> × <span class="ltr">{{ l.quantity }}</span></div> }</td>
        </ng-container>
        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
          <td mat-cell *matCellDef="let d"><span class="chip" [class.ok]="d.status === 'Posted'">{{ 'doc.status.' + d.status | t }}</span></td>
        </ng-container>
        <ng-container matColumnDef="actions">
          <th mat-header-cell *matHeaderCellDef></th>
          <td mat-cell *matCellDef="let d"><button mat-icon-button (click)="pdf(d)" [matTooltip]="'dn.print' | t"><mat-icon>print</mat-icon></button></td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
      </table>
      @if (rows().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `
})
export class DeliveriesPage {
  private readonly sales = inject(SalesApi);
  private readonly notify = inject(NotifyService);
  protected readonly columns = ['no', 'date', 'customer', 'order', 'lines', 'status', 'actions'];
  protected readonly rows = signal<DeliveryNote[]>([]);

  constructor() {
    this.sales.deliveries().subscribe((r) => this.rows.set(r));
  }

  protected pdf(d: DeliveryNote): void {
    this.sales.deliveryPdf(d.id).subscribe({ error: (e) => this.notify.error(e) });
  }
}
