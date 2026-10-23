import { downloadCsv } from '../../shared/export';
import { CURRENCIES } from '../../core/trade.models';
import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormArray, FormBuilder, FormControl, FormGroup, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { ActivatedRoute, Router } from '@angular/router';
import { filter, merge, switchMap } from 'rxjs';
import { AuthService } from '../../core/auth.service';
import { todayIso } from '../../core/dates';
import { NotifyService } from '../../core/notify.service';
import { LowStockNotice, stockLevel } from '../../core/stock-alert';
import { SalesApi } from '../../core/sales.api';
import { CreditNote, Customer, DeliveryNote, SalesInvoice, SalesInvoiceLine, SalesInvoiceStatus, SalesOrder } from '../../core/sales.models';
import { TradeApi } from '../../core/trade.api';
import { Item, Master } from '../../core/trade.models';
import { ConfirmDialog, ReverseDialog, ReverseResult } from '../../shared/dialogs';
import { SHARED } from '../../shared/shared';

export const salesInvoiceChip = (s: SalesInvoiceStatus) =>
  `chip ${s === 'Paid' ? 'ok' : s === 'Draft' ? 'warn' : s === 'Reversed' ? 'bad' : 'info'}`;

@Component({
  selector: 'app-sales-invoices',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'sinv.title' | t }}</h1>
      <button mat-stroked-button (click)="exportCsv()"><mat-icon>download</mat-icon> {{ 'export.csv' | t }}</button>
      @if (auth.can('sal.invoices:post')) {
        <a mat-flat-button routerLink="/sales/invoices/new"><mat-icon>add</mat-icon> {{ 'sinv.new' | t }}</a>
      }
    </div>
    <div class="toolbar-row">
      <mat-form-field>
        <mat-label>{{ 'cust.customer' | t }}</mat-label>
        <mat-select [formControl]="customer">
          <mat-option value="">—</mat-option>
          @for (c of customers(); track c.id) { <mat-option [value]="c.id">{{ c.code }} · {{ c | bi }}</mat-option> }
        </mat-select>
      </mat-form-field>
      <mat-slide-toggle [formControl]="openOnly">{{ 'pinv.openOnly' | t }}</mat-slide-toggle>
    </div>
    <div class="table-wrap">
      <table mat-table [dataSource]="rows()">
        <ng-container matColumnDef="no">
          <th mat-header-cell *matHeaderCellDef>{{ 'sinv.no' | t }}</th>
          <td mat-cell *matCellDef="let i"><span class="ltr">{{ i.invoiceNo }}</span><div class="secondary ltr">{{ i.customerRef }}</div></td>
        </ng-container>
        <ng-container matColumnDef="customer">
          <th mat-header-cell *matHeaderCellDef>{{ 'cust.customer' | t }}</th>
          <td mat-cell *matCellDef="let i">{{ i | bi:'customerName' }}</td>
        </ng-container>
        <ng-container matColumnDef="date">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.date' | t }}</th>
          <td mat-cell *matCellDef="let i" class="ltr">{{ i.invoiceDate | d }}<div class="secondary">{{ 'pinv.due' | t }} {{ i.dueDate | d }}</div></td>
        </ng-container>
        <ng-container matColumnDef="total">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.total' | t }}</th>
          <td mat-cell *matCellDef="let i" class="num ltr">{{ i.total | money }}</td>
        </ng-container>
        <ng-container matColumnDef="open">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'pinv.open' | t }}</th>
          <td mat-cell *matCellDef="let i" class="num ltr" [class.overdue]="i.openAmount > 0 && i.dueDate < today && i.status !== 'Draft'">
            {{ i.status === 'Draft' || i.status === 'Reversed' ? '' : (i.openAmount | money) }}</td>
        </ng-container>
        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
          <td mat-cell *matCellDef="let i"><span [class]="chip(i.status)">{{ 'sinv.status.' + i.status | t }}</span></td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns" class="clickable" (click)="router.navigate(['/sales/invoices', row.id])"></tr>
      </table>
      @if (rows().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `,
  styles: `.overdue { color: #b3261e; font-weight: 600; }`
})
export class SalesInvoicesPage {
  protected readonly auth = inject(AuthService);
  protected readonly router = inject(Router);
  private readonly sales = inject(SalesApi);
  protected readonly chip = salesInvoiceChip;
  protected readonly today = todayIso();
  protected readonly columns = ['no', 'customer', 'date', 'total', 'open', 'status'];
  protected readonly customer = new FormControl('', { nonNullable: true });
  protected readonly openOnly = new FormControl(false, { nonNullable: true });
  protected readonly customers = signal<Customer[]>([]);
  protected readonly rows = signal<SalesInvoice[]>([]);

  protected exportCsv(): void {
    downloadCsv('sales_invoices.csv', ['Invoice', 'Date', 'Due', 'Customer', 'Currency', 'Total', 'Open', 'Status'], this.rows().map((x) => [x.invoiceNo, x.invoiceDate, x.dueDate, x.customerNameEn, x.currency, x.total, x.openAmount, x.status]));
  }

  constructor() {
    this.sales.customers().subscribe((c) => this.customers.set(c));
    merge(this.customer.valueChanges, this.openOnly.valueChanges).pipe(takeUntilDestroyed()).subscribe(() => this.load());
    this.load();
  }

  private load(): void {
    this.sales.invoices({ customerId: this.customer.value || undefined, openOnly: this.openOnly.value }).subscribe((r) => this.rows.set(r));
  }
}

type LineKind = 'delivered' | 'orderService' | 'item' | 'service';

type InvoiceLineGroup = FormGroup<{
  kind: FormControl<LineKind>;
  deliveryNoteLineId: FormControl<string | null>;
  salesOrderLineId: FormControl<string | null>;
  itemId: FormControl<string | null>;
  label: FormControl<string>;
  description: FormControl<string>;
  quantity: FormControl<number | null>;
  unitPrice: FormControl<number | null>;
  discountPercent: FormControl<number | null>;
}>;

interface OpenDeliveryLine {
  dnLineId: string;
  dnNo: string;
  sku: string;
  nameEn: string;
  nameAr: string;
  remaining: number;
  orderLineId: string;
}

@Component({
  selector: 'app-sales-invoice-form',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <a mat-icon-button routerLink="/sales/invoices"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
      <h1>{{ 'sinv.new' | t }}</h1>
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
          <input matInput type="date" formControlName="invoiceDate" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'pinv.due' | t }}</mat-label>
          <input matInput type="date" formControlName="dueDate" />
          <mat-hint>{{ 'sinv.dueHint' | t }}</mat-hint>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'so.customerRef' | t }}</mat-label>
          <input matInput formControlName="customerRef" dir="ltr" />
        </mat-form-field>
        @if (needsWarehouse()) {
          <mat-form-field>
            <mat-label>{{ 'sinv.warehouse' | t }}</mat-label>
            <mat-select formControlName="warehouseId">
              @for (w of warehouses(); track w.id) { <mat-option [value]="w.id">{{ w.code }} · {{ w | bi }}</mat-option> }
            </mat-select>
          </mat-form-field>
        }
        <mat-form-field>
          <mat-label>{{ 'fx.currency' | t }}</mat-label>
          <mat-select formControlName="currency">
            @for (cur of currencies; track cur) { <mat-option [value]="cur">{{ cur }}</mat-option> }
          </mat-select>
          <mat-hint>{{ 'fx.invoiceHint' | t }}</mat-hint>
        </mat-form-field>
        @if (form.value.currency !== 'QAR') {
          <mat-form-field>
            <mat-label>{{ 'fx.rate' | t: { c: form.value.currency ?? '' } }}</mat-label>
            <input matInput type="number" min="0" step="0.0001" formControlName="exchangeRate" dir="ltr" />
          </mat-form-field>
        }
        <mat-form-field class="full">
          <mat-label>{{ 'app.notes' | t }}</mat-label>
          <input matInput formControlName="notes" />
        </mat-form-field>
      </form>

      @if (openDeliveries().length) {
        <div class="section-title">{{ 'sinv.uninvoiced' | t }}</div>
        <div class="chips-row">
          @for (d of openDeliveries(); track d.dnLineId) {
            <button mat-stroked-button type="button" (click)="addDelivered(d)" [disabled]="isAdded(d.dnLineId)">
              <mat-icon>add</mat-icon> <span class="ltr">{{ d.dnNo }}</span> · <span class="ltr">{{ d.sku }}</span> × <span class="ltr">{{ d.remaining }}</span>
            </button>
          }
        </div>
      }

      <table class="line-editor">
        <thead>
          <tr><th>{{ 'pinv.lineType' | t }}</th><th class="item-col">{{ 'po.item' | t }} / {{ 'po.description' | t }}</th><th class="narrow">{{ 'po.qty' | t }}</th>
            <th class="narrow">{{ 'po.price' | t }}</th><th class="narrow">{{ 'so.discount' | t }}</th><th class="narrow num">{{ 'po.amount' | t }}</th><th></th></tr>
        </thead>
        <tbody>
          @for (line of lines.controls; track line; let i = $index) {
            <tr [formGroup]="line">
              <td><span class="chip" [class.info]="line.controls.kind.value !== 'delivered'">{{ 'sinv.kind.' + line.controls.kind.value | t }}</span></td>
              <td>
                @switch (line.controls.kind.value) {
                  @case ('item') {
                    <mat-form-field>
                      <mat-select formControlName="itemId" (selectionChange)="priceLine(line)">
                        @for (it of items(); track it.id) { <mat-option [value]="it.id"><span class="ltr">{{ it.sku }}</span> · {{ it | bi }}</mat-option> }
                      </mat-select>
                    </mat-form-field>
                    @if (itemOf(line); as it) {
                      @if (it.itemType === 'Stock') {
                        <div class="secondary" [class]="'stock-' + level(line, it)">
                          {{ 'so.onHand' | t }}: <span class="ltr">{{ it.quantityOnHand }} {{ it.uomCode }}</span></div>
                      }
                    }
                  }
                  @case ('service') {
                    <mat-form-field><input matInput formControlName="description" [placeholder]="'po.description' | t" /></mat-form-field>
                  }
                  @default { <div>{{ line.controls.label.value }}</div> }
                }
              </td>
              <td class="narrow"><mat-form-field><input matInput type="number" min="0" formControlName="quantity" dir="ltr" /></mat-form-field></td>
              <td class="narrow"><mat-form-field><input matInput type="number" min="0" formControlName="unitPrice" dir="ltr" /></mat-form-field></td>
              <td class="narrow"><mat-form-field><input matInput type="number" min="0" max="100" formControlName="discountPercent" dir="ltr" /></mat-form-field></td>
              <td class="num ltr">{{ amount(line) | money }}</td>
              <td><button mat-icon-button type="button" (click)="lines.removeAt(i)"><mat-icon>delete</mat-icon></button></td>
            </tr>
          }
          <tr class="total-row"><td colspan="5">{{ 'po.total' | t }}</td><td class="num ltr">{{ total() | money }}</td><td></td></tr>
        </tbody>
      </table>
      <div class="actions bar">
        <button mat-button type="button" (click)="addLine('item')"><mat-icon>add</mat-icon> {{ 'sinv.addItem' | t }}</button>
        <button mat-button type="button" (click)="addLine('service')"><mat-icon>add</mat-icon> {{ 'pinv.addService' | t }}</button>
        <span class="spacer"></span>
        <button mat-stroked-button (click)="save(false)" [disabled]="invalid() || busy()">{{ 'po.saveDraft' | t }}</button>
        <button mat-flat-button (click)="save(true)" [disabled]="invalid() || busy()"><mat-icon>account_balance</mat-icon> {{ 'sinv.saveAndPost' | t }}</button>
      </div>
    </mat-card>
  `,
  styles: `
    .bar { align-items: center; margin-top: 8px; }
    .spacer { flex: 1; }
    .chips-row { display: flex; flex-wrap: wrap; gap: 8px; margin-block-end: 12px; }
  `
})
export class SalesInvoiceFormPage implements OnInit {
  private readonly sales = inject(SalesApi);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly fb = inject(FormBuilder).nonNullable;
  private readonly lowStock = inject(LowStockNotice);

  protected readonly customers = signal<Customer[]>([]);
  protected readonly items = signal<Item[]>([]);
  protected readonly warehouses = signal<Master[]>([]);
  protected readonly deliveries = signal<DeliveryNote[]>([]);
  protected readonly busy = signal(false);
  protected readonly form = this.fb.group({
    customerId: ['', Validators.required],
    invoiceDate: [todayIso(), Validators.required],
    dueDate: [''],
    customerRef: [''],
    warehouseId: [''],
    notes: [''],
    currency: ['QAR'],
    exchangeRate: [null as number | null]
  });
  protected readonly currencies = CURRENCIES;
  protected readonly lines = new FormArray<InvoiceLineGroup>([]);
  private readonly version = signal(0);
  private readonly customerId = signal('');
  private readonly orderId = signal<string | null>(null);
  protected readonly openDeliveries = computed<OpenDeliveryLine[]>(() =>
    this.deliveries()
      .filter((d) => d.status === 'Posted')
      .flatMap((d) =>
        d.lines
          .filter((l) => l.quantityInvoiced < l.quantity)
          .map((l) => ({ dnLineId: l.id, dnNo: d.dnNo, sku: l.sku, nameEn: l.nameEn, nameAr: l.nameAr, remaining: l.quantity - l.quantityInvoiced,
            orderLineId: l.salesOrderLineId }))
      )
  );
  protected readonly total = computed(() => {
    this.version();
    return this.lines.controls.reduce((s, l) => s + this.amount(l), 0);
  });
  protected readonly needsWarehouse = computed(() => {
    this.version();
    return this.lines.controls.some((l) => l.controls.kind.value === 'item' && this.items().find((i) => i.id === l.controls.itemId.value)?.itemType === 'Stock');
  });
  private orderLines = new Map<string, { unitPrice: number; discountPercent: number }>();

  constructor() {
    this.lines.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => this.version.update((v) => v + 1));
    this.form.controls.customerId.valueChanges.pipe(takeUntilDestroyed()).subscribe((id) => this.selectCustomer(id));
  }

  ngOnInit(): void {
    this.trade.items({ active: true, pageSize: 500 }).subscribe((r) => this.items.set(r.items));
    this.trade.warehouses().subscribe((w) => {
      this.warehouses.set(w.filter((x) => x.isActive));
      this.form.patchValue({ warehouseId: w.find((x) => x.isActive)?.id ?? '' });
    });
    const q = this.route.snapshot.queryParamMap;
    this.orderId.set(q.get('orderId'));
    this.sales.customers().subscribe((c) => {
      this.customers.set(c.filter((x) => x.isActive));
      const preset = q.get('customerId');
      if (preset) this.form.controls.customerId.setValue(preset);
    });
  }

  protected isAdded(dnLineId: string): boolean {
    return this.lines.controls.some((l) => l.controls.deliveryNoteLineId.value === dnLineId);
  }

  protected itemOf(line: InvoiceLineGroup): Item | undefined {
    return this.items().find((i) => i.id === line.controls.itemId.value);
  }

  /** Stock against this line: out (not enough), low (at or below the reorder level after it) or ok. */
  protected level(line: InvoiceLineGroup, it: Item): string {
    return stockLevel(it.quantityOnHand, it.reorderLevel, Number(line.controls.quantity.value) || 0);
  }

  protected amount(line: InvoiceLineGroup): number {
    const v = line.getRawValue();
    return Math.round((Number(v.quantity) || 0) * (Number(v.unitPrice) || 0) * (100 - (Number(v.discountPercent) || 0))) / 100;
  }

  protected invalid(): boolean {
    return this.form.invalid || this.lines.invalid || this.lines.length === 0 || (this.needsWarehouse() && !this.form.value.warehouseId);
  }

  protected addDelivered(d: OpenDeliveryLine): void {
    const price = this.orderLines.get(d.orderLineId);
    this.lines.push(this.line('delivered', { deliveryNoteLineId: d.dnLineId, label: `${d.dnNo} · ${d.sku} · ${d.nameEn}`, quantity: d.remaining,
      unitPrice: price?.unitPrice ?? null, discountPercent: price?.discountPercent ?? 0 }));
  }

  protected addLine(kind: LineKind, v: Partial<ReturnType<InvoiceLineGroup['getRawValue']>> = {}): void {
    this.lines.push(this.line(kind, { quantity: 1, discountPercent: 0, ...v }));
  }

  protected priceLine(line: InvoiceLineGroup): void {
    const itemId = line.controls.itemId.value;
    if (!itemId) return;
    this.sales.prices(this.customerId() || null, [itemId]).subscribe(([p]) => {
      if (p?.unitPrice !== null && p?.unitPrice !== undefined) line.controls.unitPrice.setValue(p.unitPrice);
    });
  }

  protected save(post: boolean): void {
    const v = this.form.getRawValue();
    const body = {
      customerId: v.customerId,
      invoiceDate: v.invoiceDate,
      dueDate: v.dueDate || null,
      customerRef: v.customerRef || null,
      notes: v.notes || null,
      currency: v.currency,
      exchangeRate: v.currency === 'QAR' ? null : Number(v.exchangeRate),
      warehouseId: this.needsWarehouse() ? v.warehouseId : null,
      lines: this.lines.getRawValue().map((l) => ({
        deliveryNoteLineId: l.deliveryNoteLineId,
        salesOrderLineId: l.salesOrderLineId,
        itemId: l.kind === 'item' ? l.itemId : null,
        description: l.description || null,
        quantity: Number(l.quantity),
        unitPrice: Number(l.unitPrice),
        discountPercent: Number(l.discountPercent) || 0
      }))
    };
    this.busy.set(true);
    this.sales
      .createInvoice(body)
      .pipe(switchMap((i) => (post ? this.sales.postInvoice(i.id) : [i])))
      .subscribe({
        next: (i) => {
          this.notify.success();
          if (post) this.lowStock.afterSale(i.lines.map((l) => l.itemId));
          void this.router.navigate(['/sales/invoices', i.id]);
        },
        error: (e) => {
          this.busy.set(false);
          this.notify.error(e);
        }
      });
  }

  private selectCustomer(id: string): void {
    this.customerId.set(id);
    this.lines.clear();
    this.deliveries.set([]);
    this.orderLines.clear();
    if (!id) return;
    this.sales.orders(undefined, id).subscribe((orders: SalesOrder[]) => {
      for (const o of orders) for (const l of o.lines) this.orderLines.set(l.id, { unitPrice: l.unitPrice, discountPercent: l.discountPercent });
      this.sales.deliveries({ customerId: id }).subscribe((d) => this.deliveries.set(d));
      const order = orders.find((o) => o.id === this.orderId());
      if (order)
        for (const l of order.lines.filter((x) => x.itemType === 'Service' && x.quantityInvoiced < x.quantity))
          this.addLine('orderService', { salesOrderLineId: l.id, label: `${order.soNo} · ${l.sku} · ${l.nameEn}`, quantity: l.quantity - l.quantityInvoiced,
            unitPrice: l.unitPrice, discountPercent: l.discountPercent });
    });
  }

  private line(kind: LineKind, v: Partial<ReturnType<InvoiceLineGroup['getRawValue']>>): InvoiceLineGroup {
    return this.fb.group({
      kind: this.fb.control<LineKind>(kind),
      deliveryNoteLineId: this.fb.control<string | null>(v.deliveryNoteLineId ?? null),
      salesOrderLineId: this.fb.control<string | null>(v.salesOrderLineId ?? null),
      itemId: this.fb.control<string | null>(v.itemId ?? null, kind === 'item' ? Validators.required : []),
      label: this.fb.control(v.label ?? ''),
      description: this.fb.control(v.description ?? '', kind === 'service' ? Validators.required : []),
      quantity: this.fb.control<number | null>(v.quantity ?? null, [Validators.required, Validators.min(0.000001)]),
      unitPrice: this.fb.control<number | null>(v.unitPrice ?? null, [Validators.required, Validators.min(0)]),
      discountPercent: this.fb.control<number | null>(v.discountPercent ?? 0, [Validators.min(0), Validators.max(100)])
    }) as InvoiceLineGroup;
  }
}

/** Returns (goods back into stock) and allowances (price reductions) against a posted invoice. */
@Component({
  selector: 'app-credit-note-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'scn.new' | t }} · <span class="ltr">{{ data.invoice.invoiceNo }}</span></h2>
    <mat-dialog-content>
      <p class="secondary">{{ 'scn.hint' | t }} · {{ 'pinv.open' | t }}: <b class="ltr">{{ data.invoice.openAmount | money }}</b></p>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field>
          <mat-label>{{ 'app.date' | t }}</mat-label>
          <input matInput type="date" formControlName="date" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'app.reason' | t }}</mat-label>
          <input matInput formControlName="reason" />
        </mat-form-field>
      </form>
      @if (goods.length) {
        <div class="section-title">{{ 'scn.returns' | t }}</div>
        <table class="line-editor">
          <thead><tr><th>{{ 'po.item' | t }}</th><th class="num">{{ 'scn.returnable' | t }}</th><th class="num">{{ 'po.price' | t }}</th><th class="narrow">{{ 'scn.returnQty' | t }}</th></tr></thead>
          <tbody>
            @for (l of goods; track l.id) {
              <tr>
                <td><span class="ltr">{{ l.sku }}</span> · {{ l | bi }}</td>
                <td class="num ltr">{{ l.quantity - l.quantityReturned }}</td>
                <td class="num ltr">{{ l.amount / l.quantity | money }}</td>
                <td class="narrow"><mat-form-field><input matInput type="number" min="0" [max]="l.quantity - l.quantityReturned" [formControl]="qty[l.id]" dir="ltr" /></mat-form-field></td>
              </tr>
            }
          </tbody>
        </table>
      }
      <div class="section-title">{{ 'scn.allowance' | t }}</div>
      <div class="form-grid">
        <mat-form-field>
          <mat-label>{{ 'po.description' | t }}</mat-label>
          <input matInput [formControl]="allowanceText" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'app.amount' | t }}</mat-label>
          <input matInput type="number" min="0" [formControl]="allowanceAmount" dir="ltr" />
        </mat-form-field>
      </div>
      <div class="total">{{ 'po.total' | t }}: <b class="ltr">{{ total() | money }}</b></div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="busy() || form.invalid || total() <= 0 || total() > data.invoice.openAmount" (click)="save()">{{ 'doc.post' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `.total { text-align: end; margin-top: 8px; }`
})
export class CreditNoteDialog {
  protected readonly data = inject<{ invoice: SalesInvoice }>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<CreditNoteDialog, CreditNote>);
  private readonly sales = inject(SalesApi);
  private readonly notify = inject(NotifyService);
  protected readonly busy = signal(false);
  protected readonly goods: SalesInvoiceLine[] = this.data.invoice.lines.filter((l) => l.lineType !== 'Service' && l.quantityReturned < l.quantity);
  protected readonly qty: Record<string, FormControl<number | null>> = Object.fromEntries(this.goods.map((l) => [l.id, new FormControl<number | null>(null)]));
  protected readonly allowanceText = new FormControl('', { nonNullable: true });
  protected readonly allowanceAmount = new FormControl<number | null>(null);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    date: [todayIso(), Validators.required],
    reason: ['', Validators.required]
  });
  private readonly version = signal(0);
  protected readonly total = computed(() => {
    this.version();
    const returns = this.goods.reduce((s, l) => s + Math.round((Number(this.qty[l.id].value) || 0) * (l.amount / l.quantity) * 100) / 100, 0);
    return returns + (Number(this.allowanceAmount.value) || 0);
  });

  constructor() {
    merge(this.allowanceAmount.valueChanges, ...Object.values(this.qty).map((c) => c.valueChanges))
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.version.update((v) => v + 1));
  }

  protected save(): void {
    const lines: object[] = this.goods
      .filter((l) => Number(this.qty[l.id].value) > 0)
      .map((l) => ({ lineType: 'Return', salesInvoiceLineId: l.id, quantity: Number(this.qty[l.id].value) }));
    if (Number(this.allowanceAmount.value) > 0)
      lines.push({ lineType: 'Allowance', description: this.allowanceText.value || this.form.value.reason, quantity: 1, unitPrice: Number(this.allowanceAmount.value) });
    this.busy.set(true);
    this.sales.createCreditNote({ salesInvoiceId: this.data.invoice.id, ...this.form.getRawValue(), lines }).subscribe({
      next: (n) => {
        this.notify.success();
        this.ref.close(n);
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }
}

@Component({
  selector: 'app-sales-invoice-detail',
  imports: [...SHARED],
  template: `
    @if (invoice(); as i) {
      <div class="page-header">
        <a mat-icon-button routerLink="/sales/invoices"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
        <h1>{{ 'sinv.no' | t }} <span class="ltr">{{ i.invoiceNo }}</span></h1>
        <span [class]="chip(i.status)">{{ 'sinv.status.' + i.status | t }}</span>
      </div>
      <div class="actions header-actions">
        <button mat-stroked-button (click)="pdf()"><mat-icon>picture_as_pdf</mat-icon> PDF</button>
        @if (i.status === 'Draft' && auth.can('sal.invoices:post')) {
          <button mat-flat-button (click)="post()" [disabled]="busy()"><mat-icon>account_balance</mat-icon> {{ 'pinv.post' | t }}</button>
          <button mat-button color="warn" (click)="remove()" [disabled]="busy()"><mat-icon>delete</mat-icon> {{ 'app.delete' | t }}</button>
        }
        @if ((i.status === 'Posted' || i.status === 'PartiallyPaid') && auth.can('sal.receipts:post')) {
          <a mat-flat-button routerLink="/sales/receipts" [queryParams]="{ customerId: i.customerId }"><mat-icon>payments</mat-icon> {{ 'rcpt.new' | t }}</a>
        }
        @if ((i.status === 'Posted' || i.status === 'PartiallyPaid') && auth.can('sal.invoices:post')) {
          <button mat-stroked-button (click)="creditNote(i)" [disabled]="busy()"><mat-icon>assignment_return</mat-icon> {{ 'scn.new' | t }}</button>
        }
        @if (i.status === 'Posted' && i.paidAmount === 0 && i.creditedAmount === 0 && auth.can('sal.invoices:post')) {
          <button mat-button (click)="reverse()" [disabled]="busy()"><mat-icon>undo</mat-icon> {{ 'doc.reverse' | t }}</button>
        }
      </div>
      @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
      <div class="facts">
        <span>{{ 'cust.customer' | t }}: <a [routerLink]="['/sales/customers', i.customerId]"><b>{{ i | bi:'customerName' }}</b></a></span>
        <span>{{ 'app.date' | t }}: <b class="ltr">{{ i.invoiceDate | d }}</b></span>
        <span>{{ 'pinv.due' | t }}: <b class="ltr">{{ i.dueDate | d }}</b></span>
        @if (i.customerRef) { <span>{{ 'so.customerRef' | t }}: <b class="ltr">{{ i.customerRef }}</b></span> }
        @if (i.salesOrderId) { <a [routerLink]="['/sales/orders', i.salesOrderId]">{{ 'sinv.viewOrder' | t }}</a> }
      </div>
      <div class="card-grid">
        <mat-card appearance="outlined" class="stat"><div class="label">{{ 'po.total' | t }}</div><div class="value ltr">{{ i.total | money }}</div>
          @if (i.discountTotal) { <div class="hint">{{ 'so.discount' | t }} <span class="ltr">{{ i.discountTotal | money }}</span></div> }</mat-card>
        <mat-card appearance="outlined" class="stat"><div class="label">{{ 'pinv.paid' | t }}</div><div class="value ltr">{{ i.paidAmount | money }}</div></mat-card>
        @if (i.creditedAmount) {
          <mat-card appearance="outlined" class="stat"><div class="label">{{ 'sinv.credited' | t }}</div><div class="value ltr">{{ i.creditedAmount | money }}</div></mat-card>
        }
        <mat-card appearance="outlined" class="stat"><div class="label">{{ 'pinv.open' | t }}</div><div class="value ltr">{{ i.openAmount | money }}</div></mat-card>
        @if (margin() !== null) {
          <mat-card appearance="outlined" class="stat"><div class="label">{{ 'sinv.margin' | t }}</div><div class="value ltr">{{ margin() | money }}</div>
            <div class="hint">{{ 'sinv.cost' | t }} <span class="ltr">{{ cost() | money }}</span></div></mat-card>
        }
      </div>
      <div class="table-wrap">
        <table mat-table [dataSource]="i.lines">
          <ng-container matColumnDef="type">
            <th mat-header-cell *matHeaderCellDef>{{ 'pinv.lineType' | t }}</th>
            <td mat-cell *matCellDef="let l">{{ 'sinv.type.' + l.lineType | t }}</td>
          </ng-container>
          <ng-container matColumnDef="item">
            <th mat-header-cell *matHeaderCellDef>{{ 'po.item' | t }}</th>
            <td mat-cell *matCellDef="let l">@if (l.sku) { <span class="ltr">{{ l.sku }}</span> · {{ l | bi }} } @if (l.description) { <div class="secondary">{{ l.description }}</div> }
              @if (l.quantityReturned) { <span class="chip warn">{{ 'scn.returned' | t }} <span class="ltr">{{ l.quantityReturned }}</span></span> }</td>
          </ng-container>
          <ng-container matColumnDef="qty">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.qty' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr">{{ l.quantity }}</td>
          </ng-container>
          <ng-container matColumnDef="price">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.price' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr">{{ l.unitPrice | money }}@if (l.discountPercent) { <div class="secondary">−{{ l.discountPercent }}%</div> }</td>
          </ng-container>
          <ng-container matColumnDef="amount">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.amount' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr">{{ l.amount | money }}</td>
          </ng-container>
          <tr mat-header-row *matHeaderRowDef="columns"></tr>
          <tr mat-row *matRowDef="let row; columns: columns"></tr>
        </table>
      </div>
      @if (creditNotes().length) {
        <div class="section-title">{{ 'scn.title' | t }}</div>
        <div class="table-wrap">
          <table mat-table [dataSource]="creditNotes()">
            <ng-container matColumnDef="no">
              <th mat-header-cell *matHeaderCellDef>{{ 'scn.no' | t }}</th>
              <td mat-cell *matCellDef="let n"><span class="ltr">{{ n.creditNoteNo }}</span> <span class="secondary ltr">{{ n.noteDate | d }}</span></td>
            </ng-container>
            <ng-container matColumnDef="reason">
              <th mat-header-cell *matHeaderCellDef>{{ 'app.reason' | t }}</th>
              <td mat-cell *matCellDef="let n">{{ n.reason }}</td>
            </ng-container>
            <ng-container matColumnDef="total">
              <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.total' | t }}</th>
              <td mat-cell *matCellDef="let n" class="num ltr">{{ n.total | money }}</td>
            </ng-container>
            <ng-container matColumnDef="status">
              <th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
              <td mat-cell *matCellDef="let n"><span class="chip" [class.ok]="n.status === 'Posted'">{{ 'doc.status.' + n.status | t }}</span></td>
            </ng-container>
            <ng-container matColumnDef="actions">
              <th mat-header-cell *matHeaderCellDef></th>
              <td mat-cell *matCellDef="let n">
                <button mat-icon-button (click)="creditNotePdf(n)"><mat-icon>picture_as_pdf</mat-icon></button>
                @if (n.status === 'Posted' && auth.can('sal.invoices:post')) {
                  <button mat-icon-button (click)="reverseCreditNote(n)" [matTooltip]="'doc.reverse' | t"><mat-icon>undo</mat-icon></button>
                }
              </td>
            </ng-container>
            <tr mat-header-row *matHeaderRowDef="['no', 'reason', 'total', 'status', 'actions']"></tr>
            <tr mat-row *matRowDef="let row; columns: ['no', 'reason', 'total', 'status', 'actions']"></tr>
          </table>
        </div>
      }
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: `.header-actions { margin-block: -8px 16px; }`
})
export class SalesInvoiceDetailPage implements OnInit {
  readonly id = input.required<string>();
  protected readonly auth = inject(AuthService);
  private readonly sales = inject(SalesApi);
  private readonly notify = inject(NotifyService);
  private readonly lowStock = inject(LowStockNotice);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  protected readonly chip = salesInvoiceChip;
  protected readonly columns = ['type', 'item', 'qty', 'price', 'amount'];
  protected readonly invoice = signal<SalesInvoice | null>(null);
  protected readonly creditNotes = signal<CreditNote[]>([]);
  protected readonly busy = signal(false);
  protected readonly cost = computed(() => this.invoice()?.lines.reduce((s, l) => s + l.cost, 0) ?? 0);
  protected readonly margin = computed(() => {
    const i = this.invoice();
    return !i || i.status === 'Draft' || !this.auth.can('sal.reports:read') ? null : i.total - this.cost();
  });

  ngOnInit(): void {
    this.load();
  }

  protected pdf(): void {
    this.sales.invoicePdf(this.id()).subscribe({ error: (e) => this.notify.error(e) });
  }

  protected creditNotePdf(n: CreditNote): void {
    this.sales.creditNotePdf(n.id).subscribe({ error: (e) => this.notify.error(e) });
  }

  protected post(): void {
    this.busy.set(true);
    this.sales.postInvoice(this.id()).subscribe({
      next: (i) => {
        this.busy.set(false);
        this.invoice.set(i);
        this.notify.success();
        this.lowStock.afterSale(i.lines.map((l) => l.itemId));
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }

  protected creditNote(i: SalesInvoice): void {
    this.dialog
      .open(CreditNoteDialog, { data: { invoice: i }, width: '760px' })
      .afterClosed()
      .subscribe((n?: CreditNote) => n && this.load());
  }

  protected remove(): void {
    this.dialog
      .open(ConfirmDialog, { data: { title: 'app.delete', message: 'app.delete' } })
      .afterClosed()
      .pipe(filter(Boolean), switchMap(() => this.sales.deleteInvoice(this.id())))
      .subscribe({ next: () => void this.router.navigate(['/sales/invoices']), error: (e) => this.notify.error(e) });
  }

  protected reverse(): void {
    this.dialog
      .open(ReverseDialog, { data: {} })
      .afterClosed()
      .pipe(
        filter((x): x is ReverseResult => !!x),
        switchMap((x) => this.sales.reverseInvoice(this.id(), x.date, x.reason))
      )
      .subscribe({ next: (i) => this.invoice.set(i), error: (e) => this.notify.error(e) });
  }

  protected reverseCreditNote(n: CreditNote): void {
    this.dialog
      .open(ReverseDialog, { data: {} })
      .afterClosed()
      .pipe(
        filter((x): x is ReverseResult => !!x),
        switchMap((x) => this.sales.reverseCreditNote(n.id, x.date, x.reason))
      )
      .subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  private load(): void {
    this.sales.invoice(this.id()).subscribe({ next: (i) => this.invoice.set(i), error: (e) => this.notify.error(e) });
    this.sales.creditNotes({ salesInvoiceId: this.id() }).subscribe((n) => this.creditNotes.set(n));
  }
}
