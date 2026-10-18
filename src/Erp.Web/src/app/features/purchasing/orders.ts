import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormArray, FormBuilder, FormControl, FormGroup, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { Router } from '@angular/router';
import { filter, switchMap } from 'rxjs';
import { AuthService } from '../../core/auth.service';
import { todayIso } from '../../core/dates';
import { NotifyService } from '../../core/notify.service';
import { TradeApi } from '../../core/trade.api';
import { CURRENCIES, GoodsReceipt, Item, Master, OrderStatus, PurchaseOrder, Supplier } from '../../core/trade.models';
import { ConfirmDialog, ReverseDialog, ReverseResult } from '../../shared/dialogs';
import { SHARED } from '../../shared/shared';

export const ORDER_STATUSES: OrderStatus[] = ['Draft', 'PendingApproval', 'Approved', 'PartiallyReceived', 'Received', 'Closed', 'Cancelled'];

export function orderChip(status: OrderStatus): string {
  return status === 'Received' || status === 'Closed' ? 'ok' : status === 'PendingApproval' ? 'warn' : status === 'Cancelled' ? 'bad' : 'info';
}

@Component({
  selector: 'app-orders',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'po.title' | t }}</h1>
      @if (auth.can('pur.orders:manage')) {
        <a mat-flat-button routerLink="/purchasing/orders/new"><mat-icon>add</mat-icon> {{ 'po.new' | t }}</a>
      }
    </div>
    <div class="toolbar-row">
      <mat-form-field>
        <mat-label>{{ 'app.status' | t }}</mat-label>
        <mat-select [formControl]="status">
          <mat-option value="">{{ 'po.allStatuses' | t }}</mat-option>
          @for (s of statuses; track s) { <mat-option [value]="s">{{ 'po.status.' + s | t }}</mat-option> }
        </mat-select>
      </mat-form-field>
    </div>
    <div class="table-wrap">
      <table mat-table [dataSource]="rows()">
        <ng-container matColumnDef="no">
          <th mat-header-cell *matHeaderCellDef>{{ 'po.no' | t }}</th>
          <td mat-cell *matCellDef="let o"><span class="ltr">{{ o.poNo }}</span></td>
        </ng-container>
        <ng-container matColumnDef="date">
          <th mat-header-cell *matHeaderCellDef>{{ 'po.date' | t }}</th>
          <td mat-cell *matCellDef="let o" class="ltr">{{ o.orderDate | d }}</td>
        </ng-container>
        <ng-container matColumnDef="supplier">
          <th mat-header-cell *matHeaderCellDef>{{ 'po.supplier' | t }}</th>
          <td mat-cell *matCellDef="let o">{{ o | bi:'supplierName' }}</td>
        </ng-container>
        <ng-container matColumnDef="total">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.total' | t }}</th>
          <td mat-cell *matCellDef="let o" class="num ltr">@if (o.currency !== 'QAR') { <span class="secondary">{{ o.currency }}</span> } {{ o.total | money }}</td>
        </ng-container>
        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
          <td mat-cell *matCellDef="let o"><span class="chip" [class]="orderChip(o.status)">{{ 'po.status.' + o.status | t }}</span></td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns" class="clickable" (click)="router.navigate(['/purchasing/orders', row.id])"></tr>
      </table>
      @if (rows().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `
})
export class OrdersPage {
  protected readonly auth = inject(AuthService);
  protected readonly router = inject(Router);
  private readonly trade = inject(TradeApi);
  protected readonly orderChip = (s: OrderStatus) => `chip ${orderChip(s)}`;
  protected readonly statuses = ORDER_STATUSES;
  protected readonly columns = ['no', 'date', 'supplier', 'total', 'status'];
  protected readonly status = new FormControl('', { nonNullable: true });
  protected readonly rows = signal<PurchaseOrder[]>([]);

  constructor() {
    this.status.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => this.load());
    this.load();
  }

  private load(): void {
    this.trade.orders(this.status.value || undefined).subscribe((r) => this.rows.set(r));
  }
}

type OrderLineGroup = FormGroup<{
  itemId: FormControl<string>;
  description: FormControl<string>;
  quantity: FormControl<number | null>;
  unitPrice: FormControl<number | null>;
}>;

@Component({
  selector: 'app-order-form',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <a mat-icon-button [routerLink]="id() ? ['/purchasing/orders', id()] : '/purchasing/orders'"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
      <h1>{{ (id() ? 'po.edit' : 'po.new') | t }}</h1>
    </div>
    <mat-card appearance="outlined" class="form-card">
      <form [formGroup]="form" class="form-grid">
        <mat-form-field>
          <mat-label>{{ 'po.supplier' | t }}</mat-label>
          <mat-select formControlName="supplierId">
            @for (s of suppliers(); track s.id) { <mat-option [value]="s.id"><span class="ltr">{{ s.code }}</span> · {{ s | bi }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'po.warehouse' | t }}</mat-label>
          <mat-select formControlName="warehouseId">
            @for (w of warehouses(); track w.id) { <mat-option [value]="w.id">{{ w.code }} · {{ w | bi }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'po.date' | t }}</mat-label>
          <input matInput type="date" formControlName="orderDate" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'fx.currency' | t }}</mat-label>
          <mat-select formControlName="currency">
            @for (c of currencies; track c) { <mat-option [value]="c">{{ c }}</mat-option> }
          </mat-select>
        </mat-form-field>
        @if (form.controls.currency.value !== 'QAR') {
          <mat-form-field>
            <mat-label>{{ 'fx.rate' | t: { c: form.controls.currency.value } }}</mat-label>
            <input matInput type="number" min="0" step="0.0001" formControlName="exchangeRate" dir="ltr" />
            <mat-hint>{{ 'fx.rateHint' | t }}</mat-hint>
          </mat-form-field>
        }
        <mat-form-field>
          <mat-label>{{ 'po.expected' | t }}</mat-label>
          <input matInput type="date" formControlName="expectedDate" />
        </mat-form-field>
        <mat-form-field class="full">
          <mat-label>{{ 'app.notes' | t }}</mat-label>
          <input matInput formControlName="notes" />
        </mat-form-field>
      </form>

      <table class="line-editor">
        <thead>
          <tr><th class="item-col">{{ 'po.item' | t }}</th><th>{{ 'po.description' | t }}</th><th class="narrow">{{ 'po.qty' | t }}</th><th class="narrow">{{ 'po.price' | t }}</th>
            <th class="narrow num">{{ 'po.amount' | t }}</th><th></th></tr>
        </thead>
        <tbody>
          @for (line of lines.controls; track line; let i = $index) {
            <tr [formGroup]="line">
              <td>
                <mat-form-field>
                  <mat-select formControlName="itemId" (selectionChange)="suggestPrice(line)">
                    @for (it of items(); track it.id) { <mat-option [value]="it.id"><span class="ltr">{{ it.sku }}</span> · {{ it | bi }}</mat-option> }
                  </mat-select>
                </mat-form-field>
              </td>
              <td><mat-form-field><input matInput formControlName="description" /></mat-form-field></td>
              <td class="narrow"><mat-form-field><input matInput type="number" min="0" formControlName="quantity" dir="ltr" /></mat-form-field></td>
              <td class="narrow"><mat-form-field><input matInput type="number" min="0" formControlName="unitPrice" dir="ltr" /></mat-form-field></td>
              <td class="num ltr">{{ amount(line) | money }}</td>
              <td><button mat-icon-button type="button" (click)="lines.removeAt(i)" [disabled]="lines.length === 1"><mat-icon>delete</mat-icon></button></td>
            </tr>
          }
          <tr class="total-row"><td colspan="4">{{ 'po.total' | t }} <span class="secondary">{{ form.controls.currency.value }}</span></td>
            <td class="num ltr">{{ total() | money }}</td><td></td></tr>
        </tbody>
      </table>
      <div class="actions bar">
        <button mat-button type="button" (click)="addLine()"><mat-icon>add</mat-icon> {{ 'doc.addLine' | t }}</button>
        <span class="spacer"></span>
        @if (threshold() !== null) { <span class="secondary">{{ 'po.approvalHint' | t: { n: threshold()! } }}</span> }
        <button mat-stroked-button (click)="save(false)" [disabled]="invalid() || busy()">{{ 'po.saveDraft' | t }}</button>
        <button mat-flat-button (click)="save(true)" [disabled]="invalid() || busy()"><mat-icon>send</mat-icon> {{ 'po.submit' | t }}</button>
      </div>
    </mat-card>
  `,
  styles: `
    .bar { align-items: center; margin-top: 8px; }
    .spacer { flex: 1; }
  `
})
export class OrderFormPage implements OnInit {
  readonly id = input<string>();
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  private readonly router = inject(Router);
  private readonly fb = inject(FormBuilder).nonNullable;

  protected readonly suppliers = signal<Supplier[]>([]);
  protected readonly warehouses = signal<Master[]>([]);
  protected readonly items = signal<Item[]>([]);
  protected readonly threshold = signal<number | null>(null);
  protected readonly busy = signal(false);
  protected readonly currencies = CURRENCIES;
  protected readonly form = this.fb.group({
    currency: ['QAR'],
    exchangeRate: [null as number | null],
    supplierId: ['', Validators.required],
    warehouseId: ['', Validators.required],
    orderDate: [todayIso(), Validators.required],
    expectedDate: [''],
    notes: ['']
  });
  protected readonly lines = new FormArray<OrderLineGroup>([]);
  private readonly version = signal(0);
  protected readonly total = computed(() => {
    this.version();
    return this.lines.controls.reduce((s, l) => s + this.amount(l), 0);
  });

  constructor() {
    this.lines.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => this.version.update((v) => v + 1));
    // A new order is in the supplier's currency unless changed.
    this.form.controls.supplierId.valueChanges.pipe(takeUntilDestroyed()).subscribe((id) => {
      const supplier = this.suppliers().find((s) => s.id === id);
      if (supplier && !this.id()) this.form.controls.currency.setValue(supplier.currency);
    });
  }

  ngOnInit(): void {
    this.trade.suppliers().subscribe((s) => this.suppliers.set(s.filter((x) => x.isActive)));
    this.trade.warehouses().subscribe((w) => {
      this.warehouses.set(w.filter((x) => x.isActive));
      if (!this.form.value.warehouseId) this.form.patchValue({ warehouseId: w.find((x) => x.isActive)?.id ?? '' });
    });
    this.trade.items({ active: true, pageSize: 500 }).subscribe((r) => this.items.set(r.items));
    this.trade.purchaseSettings().subscribe((s) => this.threshold.set(s.approvalThreshold));
    const id = this.id();
    if (id) {
      this.trade.order(id).subscribe((o) => {
        this.form.patchValue({ supplierId: o.supplierId, warehouseId: o.warehouseId, orderDate: o.orderDate, expectedDate: o.expectedDate ?? '',
          notes: o.notes ?? '', currency: o.currency, exchangeRate: o.currency === 'QAR' ? null : o.exchangeRate });
        for (const l of o.lines) this.addLine(l.itemId, l.description ?? '', l.quantity, l.unitPrice);
      });
    } else {
      this.addLine();
    }
  }

  protected amount(line: OrderLineGroup): number {
    const v = line.getRawValue();
    return Math.round((Number(v.quantity) || 0) * (Number(v.unitPrice) || 0) * 100) / 100;
  }

  protected invalid(): boolean {
    const v = this.form.getRawValue();
    return this.form.invalid || this.lines.invalid || this.lines.length === 0 || (v.currency !== 'QAR' && !(Number(v.exchangeRate) > 0));
  }

  protected addLine(itemId = '', description = '', quantity: number | null = null, unitPrice: number | null = null): void {
    this.lines.push(
      this.fb.group({
        itemId: this.fb.control(itemId, Validators.required),
        description: this.fb.control(description),
        quantity: this.fb.control<number | null>(quantity, [Validators.required, Validators.min(0.000001)]),
        unitPrice: this.fb.control<number | null>(unitPrice, [Validators.required, Validators.min(0)])
      }) as OrderLineGroup
    );
  }

  protected suggestPrice(line: OrderLineGroup): void {
    const item = this.items().find((i) => i.id === line.controls.itemId.value);
    if (item?.purchasePrice && line.controls.unitPrice.value === null) line.controls.unitPrice.setValue(item.purchasePrice);
  }

  protected save(submit: boolean): void {
    const v = this.form.getRawValue();
    const body = {
      ...v,
      expectedDate: v.expectedDate || null,
      exchangeRate: v.currency === 'QAR' ? null : Number(v.exchangeRate),
      lines: this.lines.getRawValue().map((l) => ({
        itemId: l.itemId, description: l.description || null, quantity: Number(l.quantity), unitPrice: Number(l.unitPrice)
      }))
    };
    this.busy.set(true);
    this.trade
      .saveOrder(this.id() ?? null, body)
      .pipe(switchMap((o) => (submit ? this.trade.orderAction(o.id, 'submit') : [o])))
      .subscribe({
        next: (o) => {
          this.notify.success();
          void this.router.navigate(['/purchasing/orders', o.id]);
        },
        error: (e) => {
          this.busy.set(false);
          this.notify.error(e);
        }
      });
  }
}

/** Quantities received now, per open stock line of the order. */
@Component({
  selector: 'app-receive-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'grn.title' | t }} · <span class="ltr">{{ data.order.poNo }}</span></h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field>
          <mat-label>{{ 'grn.date' | t }}</mat-label>
          <input matInput type="date" formControlName="receiptDate" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'grn.deliveryNote' | t }}</mat-label>
          <input matInput formControlName="supplierDeliveryNote" dir="ltr" />
        </mat-form-field>
      </form>
      <table class="line-editor">
        <thead><tr><th>{{ 'po.item' | t }}</th><th class="num">{{ 'po.qty' | t }}</th><th class="num">{{ 'grn.remaining' | t }}</th><th class="narrow">{{ 'grn.now' | t }}</th></tr></thead>
        <tbody>
          @for (l of open; track l.id) {
            <tr>
              <td><span class="ltr">{{ l.sku }}</span> · {{ l | bi }}</td>
              <td class="num ltr">{{ l.quantity }}</td>
              <td class="num ltr">{{ l.quantity - l.quantityReceived }}</td>
              <td class="narrow"><mat-form-field><input matInput type="number" min="0" [max]="l.quantity - l.quantityReceived" [formControl]="qty[l.id]" dir="ltr" /></mat-form-field></td>
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
export class ReceiveDialog {
  protected readonly data = inject<{ order: PurchaseOrder }>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<ReceiveDialog, GoodsReceipt>);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  protected readonly busy = signal(false);
  protected readonly open = this.data.order.lines.filter((l) => l.itemType === 'Stock' && l.quantityReceived < l.quantity);
  protected readonly qty: Record<string, FormControl<number | null>> = Object.fromEntries(
    this.open.map((l) => [l.id, new FormControl<number | null>(l.quantity - l.quantityReceived)])
  );
  protected readonly form = inject(FormBuilder).nonNullable.group({
    receiptDate: [todayIso(), Validators.required],
    supplierDeliveryNote: ['']
  });

  protected anything(): boolean {
    return Object.values(this.qty).some((c) => Number(c.value) > 0);
  }

  protected save(): void {
    this.busy.set(true);
    this.trade
      .createReceipt({
        purchaseOrderId: this.data.order.id,
        ...this.form.getRawValue(),
        lines: this.open.filter((l) => Number(this.qty[l.id].value) > 0).map((l) => ({ purchaseOrderLineId: l.id, quantity: Number(this.qty[l.id].value) }))
      })
      .subscribe({
        next: (r) => {
          this.notify.success();
          this.ref.close(r);
        },
        error: (e) => {
          this.busy.set(false);
          this.notify.error(e);
        }
      });
  }
}

@Component({
  selector: 'app-order-detail',
  imports: [...SHARED],
  template: `
    @if (order(); as o) {
      <div class="page-header">
        <a mat-icon-button routerLink="/purchasing/orders"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
        <h1>{{ 'po.no' | t }} <span class="ltr">{{ o.poNo }}</span></h1>
        <span [class]="chip(o.status)">{{ 'po.status.' + o.status | t }}</span>
      </div>
      <div class="actions header-actions">
        @if (o.status === 'Draft' && auth.can('pur.orders:manage')) {
          <a mat-stroked-button [routerLink]="['/purchasing/orders', o.id, 'edit']"><mat-icon>edit</mat-icon> {{ 'app.edit' | t }}</a>
          <button mat-flat-button (click)="act('submit')" [disabled]="busy()"><mat-icon>send</mat-icon> {{ 'po.submit' | t }}</button>
        }
        @if (o.status === 'PendingApproval' && auth.can('pur.orders:approve')) {
          <button mat-flat-button (click)="act('approve')" [disabled]="busy()"><mat-icon>verified</mat-icon> {{ 'po.approve' | t }}</button>
          <button mat-stroked-button (click)="act('reject')" [disabled]="busy()">{{ 'po.reject' | t }}</button>
        }
        @if ((o.status === 'Approved' || o.status === 'PartiallyReceived') && hasOpenStock() && auth.can('pur.receipts:post')) {
          <button mat-flat-button (click)="receive(o)" [disabled]="busy()"><mat-icon>move_to_inbox</mat-icon> {{ 'po.receive' | t }}</button>
        }
        @if (['Approved', 'PartiallyReceived', 'Received', 'Closed'].includes(o.status) && auth.can('pur.invoices:post')) {
          <a mat-stroked-button [routerLink]="'/purchasing/invoices/new'" [queryParams]="{ supplierId: o.supplierId, orderId: o.id }">
            <mat-icon>request_quote</mat-icon> {{ 'po.invoice' | t }}</a>
        }
        @if (['Approved', 'PartiallyReceived', 'Received'].includes(o.status) && auth.can('pur.orders:manage')) {
          <button mat-button (click)="act('close', true)" [disabled]="busy()">{{ 'po.close' | t }}</button>
        }
        @if (['Draft', 'PendingApproval', 'Approved'].includes(o.status) && auth.can('pur.orders:manage')) {
          <button mat-button color="warn" (click)="act('cancel', true)" [disabled]="busy()">{{ 'po.cancel' | t }}</button>
        }
      </div>
      @if (busy()) { <mat-progress-bar mode="indeterminate" /> }

      <div class="facts">
        <span>{{ 'po.supplier' | t }}: <b>{{ o | bi:'supplierName' }}</b></span>
        <span>{{ 'po.date' | t }}: <b class="ltr">{{ o.orderDate | d }}</b></span>
        @if (o.expectedDate) { <span>{{ 'po.expected' | t }}: <b class="ltr">{{ o.expectedDate | d }}</b></span> }
        <span>{{ 'po.warehouse' | t }}: <b>{{ warehouseName(o.warehouseId) }}</b></span>
        <span>{{ 'po.total' | t }}: <b class="ltr">{{ o.currency !== 'QAR' ? o.currency + ' ' : '' }}{{ o.total | money }}</b></span>
        @if (o.currency !== 'QAR') { <span>{{ 'fx.rateShort' | t }}: <b class="ltr">{{ o.exchangeRate }}</b></span> }
      </div>
      @if (o.notes) { <p class="secondary">{{ o.notes }}</p> }

      @if (o.gtaNotificationDue) {
        <mat-card appearance="outlined" class="form-card gta" [class.done]="!!o.gtaNotificationRef">
          <div><b>{{ 'po.gtaDue' | t }}:</b> <span class="ltr">{{ o.gtaNotificationDue | d }}</span></div>
          <div class="secondary">{{ 'po.gtaHint' | t }}</div>
          <div class="actions gta-row">
            <mat-form-field>
              <mat-label>{{ 'po.gtaRef' | t }}</mat-label>
              <input matInput [formControl]="gtaRef" dir="ltr" />
            </mat-form-field>
            <button mat-stroked-button (click)="saveGta()" [disabled]="!gtaRef.value">{{ 'po.gtaSave' | t }}</button>
          </div>
        </mat-card>
      }

      <div class="table-wrap">
        <table mat-table [dataSource]="o.lines">
          <ng-container matColumnDef="item">
            <th mat-header-cell *matHeaderCellDef>{{ 'po.item' | t }}</th>
            <td mat-cell *matCellDef="let l">
              <span class="ltr">{{ l.sku }}</span> · {{ l | bi }}
              @if (l.itemType === 'Service') { <span class="chip info">{{ 'item.type.Service' | t }}</span> }
              @if (l.whtCategory !== 'None') { <span class="chip warn">{{ 'wht.' + l.whtCategory | t }}</span> }
              @if (l.description) { <div class="secondary">{{ l.description }}</div> }
            </td>
          </ng-container>
          <ng-container matColumnDef="qty">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.qty' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr">{{ l.quantity }} <span class="secondary">{{ l.uomCode }}</span></td>
          </ng-container>
          <ng-container matColumnDef="price">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.price' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr">{{ l.unitPrice | money }}</td>
          </ng-container>
          <ng-container matColumnDef="amount">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.amount' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr">{{ l.amount | money }}</td>
          </ng-container>
          <ng-container matColumnDef="received">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.received' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr">{{ l.itemType === 'Stock' ? l.quantityReceived : '—' }}</td>
          </ng-container>
          <ng-container matColumnDef="invoiced">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.invoiced' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr">{{ l.quantityInvoiced }}</td>
          </ng-container>
          <tr mat-header-row *matHeaderRowDef="columns"></tr>
          <tr mat-row *matRowDef="let row; columns: columns"></tr>
        </table>
      </div>

      @if (receipts().length) {
        <div class="section-title">{{ 'po.receipts' | t }}</div>
        <div class="table-wrap">
          <table mat-table [dataSource]="receipts()">
            <ng-container matColumnDef="no">
              <th mat-header-cell *matHeaderCellDef>{{ 'grn.no' | t }}</th>
              <td mat-cell *matCellDef="let r"><span class="ltr">{{ r.grnNo }}</span></td>
            </ng-container>
            <ng-container matColumnDef="date">
              <th mat-header-cell *matHeaderCellDef>{{ 'grn.date' | t }}</th>
              <td mat-cell *matCellDef="let r" class="ltr">{{ r.receiptDate | d }}</td>
            </ng-container>
            <ng-container matColumnDef="lines">
              <th mat-header-cell *matHeaderCellDef>{{ 'doc.lines' | t }}</th>
              <td mat-cell *matCellDef="let r">
                @for (l of r.lines; track l.id) { <div><span class="ltr">{{ l.sku }}</span> × <span class="ltr">{{ l.quantity }}</span></div> }
              </td>
            </ng-container>
            <ng-container matColumnDef="value">
              <th mat-header-cell *matHeaderCellDef class="num">{{ 'grn.value' | t }}</th>
              <td mat-cell *matCellDef="let r" class="num ltr">{{ r.totalValue | money }}</td>
            </ng-container>
            <ng-container matColumnDef="status">
              <th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
              <td mat-cell *matCellDef="let r"><span class="chip" [class.ok]="r.status === 'Posted'">{{ 'doc.status.' + r.status | t }}</span></td>
            </ng-container>
            <ng-container matColumnDef="actions">
              <th mat-header-cell *matHeaderCellDef></th>
              <td mat-cell *matCellDef="let r">
                @if (r.status === 'Posted' && auth.can('pur.receipts:post')) {
                  <button mat-icon-button (click)="reverseReceipt(r)" [matTooltip]="'doc.reverse' | t"><mat-icon>undo</mat-icon></button>
                }
              </td>
            </ng-container>
            <tr mat-header-row *matHeaderRowDef="receiptColumns"></tr>
            <tr mat-row *matRowDef="let row; columns: receiptColumns"></tr>
          </table>
        </div>
      }
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: `
    .header-actions { margin-block: -8px 16px; }
    .gta { border-color: #e8b04a; }
    .gta.done { border-color: var(--mat-sys-outline-variant); }
    .gta-row { align-items: center; margin-top: 8px; }
  `
})
export class OrderDetailPage implements OnInit {
  readonly id = input.required<string>();
  protected readonly auth = inject(AuthService);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  private readonly dialog = inject(MatDialog);

  protected readonly columns = ['item', 'qty', 'price', 'amount', 'received', 'invoiced'];
  protected readonly receiptColumns = ['no', 'date', 'lines', 'value', 'status', 'actions'];
  protected readonly order = signal<PurchaseOrder | null>(null);
  protected readonly receipts = signal<GoodsReceipt[]>([]);
  protected readonly warehouses = signal<Master[]>([]);
  protected readonly busy = signal(false);
  protected readonly gtaRef = new FormControl('', { nonNullable: true });
  protected readonly chip = (s: OrderStatus) => `chip ${orderChip(s)}`;
  protected readonly hasOpenStock = computed(() => !!this.order()?.lines.some((l) => l.itemType === 'Stock' && l.quantityReceived < l.quantity));

  ngOnInit(): void {
    this.trade.warehouses().subscribe((w) => this.warehouses.set(w));
    this.load();
  }

  protected warehouseName(id: string): string {
    const w = this.warehouses().find((x) => x.id === id);
    return w ? `${w.code}` : '';
  }

  protected act(action: 'submit' | 'approve' | 'reject' | 'cancel' | 'close', confirm = false): void {
    const run = () => {
      this.busy.set(true);
      this.trade.orderAction(this.id(), action).subscribe({
        next: (o) => {
          this.busy.set(false);
          this.order.set(o);
          this.notify.success();
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

  protected receive(o: PurchaseOrder): void {
    this.dialog
      .open(ReceiveDialog, { data: { order: o }, width: '760px' })
      .afterClosed()
      .subscribe((r?: GoodsReceipt) => r && this.load());
  }

  protected reverseReceipt(r: GoodsReceipt): void {
    this.dialog
      .open(ReverseDialog, { data: {} })
      .afterClosed()
      .pipe(
        filter((x): x is ReverseResult => !!x),
        switchMap((x) => this.trade.reverseReceipt(r.id, x.date, x.reason))
      )
      .subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  protected saveGta(): void {
    this.trade.setGtaNotification(this.id(), this.gtaRef.value).subscribe({
      next: (o) => {
        this.order.set(o);
        this.notify.success();
      },
      error: (e) => this.notify.error(e)
    });
  }

  private load(): void {
    this.trade.order(this.id()).subscribe({
      next: (o) => {
        this.order.set(o);
        this.gtaRef.setValue(o.gtaNotificationRef ?? '');
      },
      error: (e) => this.notify.error(e)
    });
    this.trade.receipts({ purchaseOrderId: this.id() }).subscribe((r) => this.receipts.set(r));
  }
}
