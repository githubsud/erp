import { downloadCsv } from '../../shared/export';
import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormArray, FormBuilder, FormControl, FormGroup, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { ActivatedRoute, Router } from '@angular/router';
import { filter, merge, switchMap } from 'rxjs';
import { AuthService } from '../../core/auth.service';
import { todayIso } from '../../core/dates';
import { NotifyService } from '../../core/notify.service';
import { TradeApi } from '../../core/trade.api';
import {
  CURRENCIES, DebitNote, GoodsReceipt, InvoiceLine, InvoiceLineType, InvoiceStatus, Item, MatchStatus, PurchaseOrder, Supplier, SupplierInvoice, WHT_CATEGORIES,
  WhtCategory
} from '../../core/trade.models';
import { ConfirmDialog, ReverseDialog, ReverseResult } from '../../shared/dialogs';
import { SHARED } from '../../shared/shared';

export function invoiceChip(status: InvoiceStatus): string {
  return `chip ${status === 'Paid' ? 'ok' : status === 'Draft' ? 'warn' : status === 'Reversed' ? 'bad' : 'info'}`;
}

export function matchChip(status: MatchStatus): string {
  return `chip ${status === 'Variance' ? 'bad' : status === 'Matched' || status === 'Approved' ? 'ok' : ''}`;
}

@Component({
  selector: 'app-invoices',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'pinv.title' | t }}</h1>
      <button mat-stroked-button (click)="exportCsv()"><mat-icon>download</mat-icon> {{ 'export.csv' | t }}</button>
      @if (auth.can('pur.invoices:post')) {
        <a mat-flat-button routerLink="/purchasing/invoices/new"><mat-icon>add</mat-icon> {{ 'pinv.new' | t }}</a>
      }
    </div>
    <div class="toolbar-row">
      <mat-form-field>
        <mat-label>{{ 'po.supplier' | t }}</mat-label>
        <mat-select [formControl]="supplier">
          <mat-option value="">—</mat-option>
          @for (s of suppliers(); track s.id) { <mat-option [value]="s.id">{{ s.code }} · {{ s | bi }}</mat-option> }
        </mat-select>
      </mat-form-field>
      <mat-slide-toggle [formControl]="openOnly">{{ 'pinv.openOnly' | t }}</mat-slide-toggle>
    </div>
    <div class="table-wrap">
      <table mat-table [dataSource]="rows()">
        <ng-container matColumnDef="no">
          <th mat-header-cell *matHeaderCellDef>{{ 'pinv.no' | t }}</th>
          <td mat-cell *matCellDef="let i"><span class="ltr">{{ i.invoiceNo }}</span><div class="secondary ltr">{{ i.supplierRef }}</div></td>
        </ng-container>
        <ng-container matColumnDef="supplier">
          <th mat-header-cell *matHeaderCellDef>{{ 'po.supplier' | t }}</th>
          <td mat-cell *matCellDef="let i">{{ i | bi:'supplierName' }}</td>
        </ng-container>
        <ng-container matColumnDef="date">
          <th mat-header-cell *matHeaderCellDef>{{ 'pinv.date' | t }}</th>
          <td mat-cell *matCellDef="let i" class="ltr">{{ i.invoiceDate | d }}<div class="secondary">{{ 'pinv.due' | t }} {{ i.dueDate | d }}</div></td>
        </ng-container>
        <ng-container matColumnDef="total">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.total' | t }}</th>
          <td mat-cell *matCellDef="let i" class="num ltr">{{ i.total | money }}</td>
        </ng-container>
        <ng-container matColumnDef="open">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'pinv.open' | t }}</th>
          <td mat-cell *matCellDef="let i" class="num ltr">{{ i.status === 'Draft' || i.status === 'Reversed' ? '' : (i.openAmount | money) }}</td>
        </ng-container>
        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
          <td mat-cell *matCellDef="let i">
            <span [class]="invoiceChip(i.status)">{{ 'pinv.status.' + i.status | t }}</span>
            @if (i.matchStatus === 'Variance') { <span [class]="matchChip(i.matchStatus)">{{ 'pinv.match.Variance' | t }}</span> }
          </td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns" class="clickable" (click)="router.navigate(['/purchasing/invoices', row.id])"></tr>
      </table>
      @if (rows().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `
})
export class InvoicesPage {
  protected readonly auth = inject(AuthService);
  protected readonly router = inject(Router);
  private readonly trade = inject(TradeApi);
  protected readonly invoiceChip = invoiceChip;
  protected readonly matchChip = matchChip;
  protected readonly columns = ['no', 'supplier', 'date', 'total', 'open', 'status'];
  protected readonly supplier = new FormControl('', { nonNullable: true });
  protected readonly openOnly = new FormControl(false, { nonNullable: true });
  protected readonly suppliers = signal<Supplier[]>([]);
  protected readonly rows = signal<SupplierInvoice[]>([]);

  protected exportCsv(): void {
    downloadCsv('supplier_invoices.csv', ['Invoice', 'Supplier ref', 'Date', 'Due', 'Supplier', 'Currency', 'Total', 'Open', 'Status'], this.rows().map((x) => [x.invoiceNo, x.supplierRef, x.invoiceDate, x.dueDate, x.supplierNameEn, x.currency, x.total, x.openAmount, x.status]));
  }

  constructor() {
    this.trade.suppliers().subscribe((s) => this.suppliers.set(s));
    merge(this.supplier.valueChanges, this.openOnly.valueChanges).pipe(takeUntilDestroyed()).subscribe(() => this.load());
    this.load();
  }

  private load(): void {
    this.trade.invoices({ supplierId: this.supplier.value || undefined, openOnly: this.openOnly.value }).subscribe((r) => this.rows.set(r));
  }
}

type InvoiceLineGroup = FormGroup<{
  lineType: FormControl<InvoiceLineType>;
  goodsReceiptLineId: FormControl<string | null>;
  purchaseOrderLineId: FormControl<string | null>;
  itemId: FormControl<string | null>;
  label: FormControl<string>;
  description: FormControl<string>;
  quantity: FormControl<number | null>;
  unitPrice: FormControl<number | null>;
  receiptCost: FormControl<number | null>;
  whtCategory: FormControl<WhtCategory | null>;
}>;

interface OpenReceiptLine {
  grnLineId: string;
  grnNo: string;
  sku: string;
  nameEn: string;
  nameAr: string;
  remaining: number;
  unitCost: number;
}

@Component({
  selector: 'app-invoice-form',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <a mat-icon-button routerLink="/purchasing/invoices"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
      <h1>{{ 'pinv.new' | t }}</h1>
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
          <mat-label>{{ 'pinv.ref' | t }}</mat-label>
          <input matInput formControlName="supplierRef" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'pinv.date' | t }}</mat-label>
          <input matInput type="date" formControlName="invoiceDate" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'pinv.due' | t }}</mat-label>
          <input matInput type="date" formControlName="dueDate" />
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
            <mat-hint>{{ 'fx.invoiceRateHint' | t }}</mat-hint>
          </mat-form-field>
        }
        <mat-form-field class="full">
          <mat-label>{{ 'app.notes' | t }}</mat-label>
          <input matInput formControlName="notes" />
        </mat-form-field>
      </form>
      @if (selectedSupplier()?.subjectToWht) { <div class="error-panel">{{ 'pinv.whtHint' | t }}</div> }

      @if (openReceipts().length) {
        <div class="section-title">{{ 'pinv.uninvoiced' | t }}</div>
        <div class="receipts">
          @for (r of openReceipts(); track r.grnLineId) {
            <button mat-stroked-button type="button" (click)="addGoods(r)" [disabled]="isAdded(r.grnLineId)">
              <mat-icon>add</mat-icon> <span class="ltr">{{ r.grnNo }}</span> · <span class="ltr">{{ r.sku }}</span> × <span class="ltr">{{ r.remaining }}</span>
            </button>
          }
        </div>
      }

      <table class="line-editor">
        <thead>
          <tr><th>{{ 'pinv.lineType' | t }}</th><th>{{ 'po.item' | t }} / {{ 'po.description' | t }}</th><th class="narrow">{{ 'po.qty' | t }}</th>
            <th class="narrow">{{ 'po.price' | t }}</th>@if (selectedSupplier()?.subjectToWht) { <th>{{ 'pinv.whtCategory' | t }}</th> }
            <th class="narrow num">{{ 'po.amount' | t }}</th><th></th></tr>
        </thead>
        <tbody>
          @for (line of lines.controls; track line; let i = $index) {
            <tr [formGroup]="line">
              <td><span class="chip" [class.info]="line.controls.lineType.value !== 'Goods'">{{ 'pinv.type.' + line.controls.lineType.value | t }}</span></td>
              <td>
                @switch (line.controls.lineType.value) {
                  @case ('Goods') {
                    <div>{{ line.controls.label.value }}</div>
                    <div class="secondary">{{ 'pinv.receiptCost' | t }}: <span class="ltr">{{ line.controls.receiptCost.value | money }}</span></div>
                  }
                  @case ('Service') {
                    <div class="service">
                      <mat-form-field>
                        <mat-select formControlName="itemId" [placeholder]="'po.item' | t">
                          <mat-option [value]="null">—</mat-option>
                          @for (it of serviceItems(); track it.id) { <mat-option [value]="it.id"><span class="ltr">{{ it.sku }}</span> · {{ it | bi }}</mat-option> }
                        </mat-select>
                      </mat-form-field>
                      <mat-form-field><input matInput formControlName="description" [placeholder]="'po.description' | t" /></mat-form-field>
                    </div>
                  }
                  @default {
                    <mat-form-field><input matInput formControlName="description" [placeholder]="'po.description' | t" /></mat-form-field>
                  }
                }
              </td>
              <td class="narrow"><mat-form-field><input matInput type="number" min="0" formControlName="quantity" dir="ltr" /></mat-form-field></td>
              <td class="narrow"><mat-form-field><input matInput type="number" min="0" formControlName="unitPrice" dir="ltr" /></mat-form-field></td>
              @if (selectedSupplier()?.subjectToWht) {
                <td>
                  @if (line.controls.lineType.value !== 'Goods') {
                    <mat-form-field>
                      <mat-select formControlName="whtCategory">
                        <mat-option [value]="null">{{ 'wht.' + (selectedSupplier()?.defaultWhtCategory ?? 'None') | t }}</mat-option>
                        @for (w of whtCategories; track w) { <mat-option [value]="w">{{ 'wht.' + w | t }}</mat-option> }
                      </mat-select>
                    </mat-form-field>
                  }
                </td>
              }
              <td class="num ltr">{{ amount(line) | money }}</td>
              <td><button mat-icon-button type="button" (click)="lines.removeAt(i)"><mat-icon>delete</mat-icon></button></td>
            </tr>
          }
          <tr class="total-row"><td colspan="4">{{ 'po.total' | t }}</td>@if (selectedSupplier()?.subjectToWht) { <td></td> }
            <td class="num ltr">{{ total() | money }}</td><td></td></tr>
        </tbody>
      </table>
      <div class="actions bar">
        <button mat-button type="button" (click)="addLine('Service')"><mat-icon>add</mat-icon> {{ 'pinv.addService' | t }}</button>
        <button mat-button type="button" (click)="addLine('LandedCost')"><mat-icon>add</mat-icon> {{ 'pinv.addLanded' | t }}</button>
        <span class="spacer"></span>
        @if (form.controls.currency.value !== 'QAR' && form.controls.exchangeRate.value) {
          <span class="secondary">≈ QAR <span class="ltr">{{ total() * form.controls.exchangeRate.value | money }}</span></span>
        }
        <button mat-flat-button (click)="save()"
          [disabled]="form.invalid || lines.invalid || lines.length === 0 || busy() || (form.controls.currency.value !== 'QAR' && !form.controls.exchangeRate.value)">
          {{ 'app.save' | t }}</button>
      </div>
    </mat-card>
  `,
  styles: `
    .bar { align-items: center; margin-top: 8px; }
    .spacer { flex: 1; }
    .receipts { display: flex; flex-wrap: wrap; gap: 8px; margin-block-end: 12px; }
    .service { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
  `
})
export class InvoiceFormPage implements OnInit {
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly fb = inject(FormBuilder).nonNullable;

  protected readonly whtCategories = WHT_CATEGORIES;
  protected readonly suppliers = signal<Supplier[]>([]);
  protected readonly serviceItems = signal<Item[]>([]);
  protected readonly receipts = signal<GoodsReceipt[]>([]);
  protected readonly busy = signal(false);
  protected readonly currencies = CURRENCIES;
  protected readonly form = this.fb.group({
    currency: ['QAR'],
    exchangeRate: [null as number | null],
    supplierId: ['', Validators.required],
    supplierRef: ['', Validators.required],
    invoiceDate: [todayIso(), Validators.required],
    dueDate: [''],
    notes: ['']
  });
  protected readonly lines = new FormArray<InvoiceLineGroup>([]);
  private readonly supplierId = signal('');
  private readonly orderId = signal<string | null>(null);
  private readonly version = signal(0);
  protected readonly selectedSupplier = computed(() => this.suppliers().find((s) => s.id === this.supplierId()) ?? null);
  protected readonly openReceipts = computed<OpenReceiptLine[]>(() =>
    this.receipts()
      .filter((r) => r.status === 'Posted' && (!this.orderId() || r.purchaseOrderId === this.orderId()))
      .flatMap((r) =>
        r.lines
          .filter((l) => l.quantityInvoiced < l.quantity)
          .map((l) => ({ grnLineId: l.id, grnNo: r.grnNo, sku: l.sku, nameEn: l.nameEn, nameAr: l.nameAr, remaining: l.quantity - l.quantityInvoiced, unitCost: l.unitCost }))
      )
  );
  protected readonly total = computed(() => {
    this.version();
    return this.lines.controls.reduce((s, l) => s + this.amount(l), 0);
  });

  constructor() {
    this.lines.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => this.version.update((v) => v + 1));
    this.form.controls.supplierId.valueChanges.pipe(takeUntilDestroyed()).subscribe((id) => this.selectSupplier(id));
  }

  ngOnInit(): void {
    this.trade.items({ type: 'Service', active: true, pageSize: 500 }).subscribe((r) => this.serviceItems.set(r.items));
    const q = this.route.snapshot.queryParamMap;
    this.orderId.set(q.get('orderId'));
    this.trade.suppliers().subscribe((s) => {
      this.suppliers.set(s.filter((x) => x.isActive));
      const supplierId = q.get('supplierId');
      if (supplierId) this.form.controls.supplierId.setValue(supplierId);
    });
  }

  protected isAdded(grnLineId: string): boolean {
    return this.lines.controls.some((l) => l.controls.goodsReceiptLineId.value === grnLineId);
  }

  protected amount(line: InvoiceLineGroup): number {
    const v = line.getRawValue();
    return Math.round((Number(v.quantity) || 0) * (Number(v.unitPrice) || 0) * 100) / 100;
  }

  protected addGoods(r: OpenReceiptLine): void {
    this.lines.push(this.line('Goods', { goodsReceiptLineId: r.grnLineId, label: `${r.grnNo} · ${r.sku} · ${r.nameEn}`, quantity: r.remaining,
      unitPrice: r.unitCost, receiptCost: r.unitCost }));
  }

  protected addLine(type: InvoiceLineType, values: Partial<ReturnType<InvoiceLineGroup['getRawValue']>> = {}): void {
    this.lines.push(this.line(type, { quantity: 1, ...values }));
  }

  protected save(): void {
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.trade
      .createInvoice({
        ...v,
        dueDate: v.dueDate || null,
        exchangeRate: v.currency === 'QAR' ? null : Number(v.exchangeRate),
        purchaseOrderId: this.orderId(),
        lines: this.lines.getRawValue().map((l) => ({
          lineType: l.lineType,
          goodsReceiptLineId: l.goodsReceiptLineId,
          purchaseOrderLineId: l.purchaseOrderLineId,
          itemId: l.lineType === 'Service' ? l.itemId : null,
          description: l.description || null,
          quantity: Number(l.quantity),
          unitPrice: Number(l.unitPrice),
          whtCategory: l.whtCategory
        }))
      })
      .subscribe({
        next: (i) => {
          this.notify.success();
          void this.router.navigate(['/purchasing/invoices', i.id]);
        },
        error: (e) => {
          this.busy.set(false);
          this.notify.error(e);
        }
      });
  }

  private selectSupplier(id: string): void {
    this.supplierId.set(id);
    const supplier = this.suppliers().find((s) => s.id === id);
    if (supplier) this.form.controls.currency.setValue(supplier.currency);
    this.lines.clear();
    this.receipts.set([]);
    if (!id) return;
    this.trade.receipts({ supplierId: id }).subscribe((r) => this.receipts.set(r));
    const orderId = this.orderId();
    if (orderId) {
      // Service lines of the order that are not fully invoiced yet.
      this.trade.order(orderId).subscribe((o: PurchaseOrder) => {
        if (o.supplierId !== id) return;
        for (const l of o.lines.filter((x) => x.itemType === 'Service' && x.quantityInvoiced < x.quantity))
          this.addLine('Service', { purchaseOrderLineId: l.id, itemId: l.itemId, description: l.description ?? l.nameEn,
            quantity: l.quantity - l.quantityInvoiced, unitPrice: l.unitPrice });
      });
    }
  }

  private line(type: InvoiceLineType, v: Partial<ReturnType<InvoiceLineGroup['getRawValue']>>): InvoiceLineGroup {
    return this.fb.group({
      lineType: this.fb.control<InvoiceLineType>(type),
      goodsReceiptLineId: this.fb.control<string | null>(v.goodsReceiptLineId ?? null),
      purchaseOrderLineId: this.fb.control<string | null>(v.purchaseOrderLineId ?? null),
      itemId: this.fb.control<string | null>(v.itemId ?? null),
      label: this.fb.control(v.label ?? ''),
      description: this.fb.control(v.description ?? '', type === 'LandedCost' ? Validators.required : []),
      quantity: this.fb.control<number | null>(v.quantity ?? null, [Validators.required, Validators.min(0.000001)]),
      unitPrice: this.fb.control<number | null>(v.unitPrice ?? null, [Validators.required, Validators.min(0)]),
      receiptCost: this.fb.control<number | null>(v.receiptCost ?? null),
      whtCategory: this.fb.control<WhtCategory | null>(v.whtCategory ?? null)
    }) as InvoiceLineGroup;
  }
}

/** Goods sent back to the supplier and price allowances received, against a posted invoice. */
@Component({
  selector: 'app-debit-note-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'pdn.new' | t }} · <span class="ltr">{{ data.invoice.invoiceNo }}</span></h2>
    <mat-dialog-content>
      <p class="secondary">{{ 'pdn.hint' | t }} · {{ 'pinv.open' | t }}: <b class="ltr">{{ data.invoice.openAmount | money }}</b></p>
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
        <div class="section-title">{{ 'pdn.returns' | t }}</div>
        <table class="line-editor">
          <thead><tr><th>{{ 'po.item' | t }}</th><th class="num">{{ 'scn.returnable' | t }}</th><th class="num">{{ 'po.price' | t }}</th><th class="narrow">{{ 'scn.returnQty' | t }}</th></tr></thead>
          <tbody>
            @for (l of goods; track l.id) {
              <tr>
                <td><span class="ltr">{{ l.sku }}</span> {{ l.description }}</td>
                <td class="num ltr">{{ l.quantity - l.quantityReturned }}</td>
                <td class="num ltr">{{ l.unitPrice | money }}</td>
                <td class="narrow"><mat-form-field><input matInput type="number" min="0" [max]="l.quantity - l.quantityReturned" [formControl]="qty[l.id]" dir="ltr" /></mat-form-field></td>
              </tr>
            }
          </tbody>
        </table>
      }
      <div class="section-title">{{ 'pdn.allowance' | t }}</div>
      <div class="form-grid">
        <mat-form-field>
          <mat-label>{{ 'po.item' | t }}</mat-label>
          <mat-select [formControl]="allowanceLine">
            <mat-option [value]="null">—</mat-option>
            @for (l of data.invoice.lines; track l.id) { <mat-option [value]="l.id"><span class="ltr">{{ l.sku ?? l.lineNo }}</span> {{ l.description }}</mat-option> }
          </mat-select>
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
export class DebitNoteDialog {
  protected readonly data = inject<{ invoice: SupplierInvoice }>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<DebitNoteDialog, DebitNote>);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  protected readonly busy = signal(false);
  protected readonly goods: InvoiceLine[] = this.data.invoice.lines.filter((l) => l.lineType === 'Goods' && l.quantityReturned < l.quantity);
  protected readonly qty: Record<string, FormControl<number | null>> = Object.fromEntries(this.goods.map((l) => [l.id, new FormControl<number | null>(null)]));
  protected readonly allowanceLine = new FormControl<string | null>(null);
  protected readonly allowanceAmount = new FormControl<number | null>(null);
  protected readonly form = inject(FormBuilder).nonNullable.group({ date: [todayIso(), Validators.required], reason: ['', Validators.required] });
  private readonly version = signal(0);
  protected readonly total = computed(() => {
    this.version();
    const returns = this.goods.reduce((s, l) => s + Math.round((Number(this.qty[l.id].value) || 0) * l.unitPrice * 100) / 100, 0);
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
      .map((l) => ({ lineType: 'Return', supplierInvoiceLineId: l.id, quantity: Number(this.qty[l.id].value) }));
    if (Number(this.allowanceAmount.value) > 0)
      lines.push({ lineType: 'Allowance', supplierInvoiceLineId: this.allowanceLine.value, description: this.form.value.reason, quantity: 1,
        unitPrice: Number(this.allowanceAmount.value) });
    this.busy.set(true);
    this.trade.createDebitNote({ supplierInvoiceId: this.data.invoice.id, ...this.form.getRawValue(), lines }).subscribe({
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
  selector: 'app-invoice-detail',
  imports: [...SHARED],
  template: `
    @if (invoice(); as i) {
      <div class="page-header">
        <a mat-icon-button routerLink="/purchasing/invoices"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
        <h1>{{ 'pinv.no' | t }} <span class="ltr">{{ i.invoiceNo }}</span></h1>
        <span [class]="invoiceChip(i.status)">{{ 'pinv.status.' + i.status | t }}</span>
        @if (i.matchStatus !== 'NotApplicable') { <span [class]="matchChip(i.matchStatus)">{{ 'pinv.match.' + i.matchStatus | t }}</span> }
      </div>
      <div class="actions header-actions">
        @if (i.status === 'Draft') {
          @if (i.matchStatus === 'Variance' && auth.can('pur.invoices:approve')) {
            <button mat-flat-button (click)="act('approve-variance')" [disabled]="busy()"><mat-icon>verified</mat-icon> {{ 'pinv.approveVariance' | t }}</button>
          }
          @if (i.matchStatus !== 'Variance' && auth.can('pur.invoices:post')) {
            <button mat-flat-button (click)="act('post')" [disabled]="busy()"><mat-icon>account_balance</mat-icon> {{ 'pinv.post' | t }}</button>
          }
          @if (auth.can('pur.invoices:post')) {
            <button mat-button color="warn" (click)="remove()" [disabled]="busy()"><mat-icon>delete</mat-icon> {{ 'app.delete' | t }}</button>
          }
        }
        @if ((i.status === 'Posted' || i.status === 'PartiallyPaid') && !i.whtAmount && auth.can('pur.invoices:post')) {
          <button mat-stroked-button (click)="debitNote(i)" [disabled]="busy()"><mat-icon>assignment_return</mat-icon> {{ 'pdn.new' | t }}</button>
        }
        @if (i.status === 'Posted' && !i.debitedAmount && auth.can('pur.invoices:post')) {
          <button mat-button (click)="reverse()" [disabled]="busy()"><mat-icon>undo</mat-icon> {{ 'doc.reverse' | t }}</button>
        }
        @if ((i.status === 'Posted' || i.status === 'PartiallyPaid') && auth.can('pur.payments:post')) {
          <a mat-stroked-button routerLink="/purchasing/payments" [queryParams]="{ supplierId: i.supplierId }"><mat-icon>price_check</mat-icon> {{ 'pay.new' | t }}</a>
        }
      </div>
      @if (i.matchStatus === 'Variance') { <div class="error-panel">{{ 'pinv.varianceHint' | t }}</div> }
      @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
      <div class="facts">
        <span>{{ 'po.supplier' | t }}: <b>{{ i | bi:'supplierName' }}</b></span>
        <span>{{ 'pinv.ref' | t }}: <b class="ltr">{{ i.supplierRef }}</b></span>
        <span>{{ 'pinv.date' | t }}: <b class="ltr">{{ i.invoiceDate | d }}</b></span>
        <span>{{ 'pinv.due' | t }}: <b class="ltr">{{ i.dueDate | d }}</b></span>
        @if (i.currency !== 'QAR') {
          <span>{{ 'fx.currency' | t }}: <b class="ltr">{{ i.currency }} @ {{ i.exchangeRate }}</b></span>
          <span>QAR: <b class="ltr">{{ i.totalBase | money }}</b> · {{ 'pinv.open' | t }} <b class="ltr">{{ i.openBase | money }}</b></span>
        }
      </div>
      <div class="card-grid">
        <mat-card appearance="outlined" class="stat"><div class="label">{{ 'po.total' | t }}</div><div class="value ltr">{{ i.total | money }}</div></mat-card>
        @if (i.whtAmount) {
          <mat-card appearance="outlined" class="stat"><div class="label">{{ 'pinv.wht' | t }}</div><div class="value ltr">{{ i.whtAmount | money }}</div>
            <div class="hint">{{ 'pinv.whtHint' | t }}</div></mat-card>
        }
        <mat-card appearance="outlined" class="stat"><div class="label">{{ 'pinv.paid' | t }}</div><div class="value ltr">{{ i.paidAmount | money }}</div></mat-card>
        @if (i.debitedAmount) {
          <mat-card appearance="outlined" class="stat"><div class="label">{{ 'pdn.debited' | t }}</div><div class="value ltr">{{ i.debitedAmount | money }}</div></mat-card>
        }
        <mat-card appearance="outlined" class="stat"><div class="label">{{ 'pinv.open' | t }}</div><div class="value ltr">{{ i.openAmount | money }}</div></mat-card>
      </div>
      <div class="table-wrap">
        <table mat-table [dataSource]="i.lines">
          <ng-container matColumnDef="type">
            <th mat-header-cell *matHeaderCellDef>{{ 'pinv.lineType' | t }}</th>
            <td mat-cell *matCellDef="let l">{{ 'pinv.type.' + l.lineType | t }}</td>
          </ng-container>
          <ng-container matColumnDef="item">
            <th mat-header-cell *matHeaderCellDef>{{ 'po.item' | t }}</th>
            <td mat-cell *matCellDef="let l"><span class="ltr">{{ l.sku }}</span> {{ l.description }}</td>
          </ng-container>
          <ng-container matColumnDef="qty">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.qty' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr">{{ l.quantity }}</td>
          </ng-container>
          <ng-container matColumnDef="price">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.price' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr">{{ l.unitPrice | money }}
              @if (l.receiptUnitCost !== null) { <div class="secondary">{{ 'pinv.receiptCost' | t }} {{ l.receiptUnitCost | money }}</div> }</td>
          </ng-container>
          <ng-container matColumnDef="variance">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'pinv.variance' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr" [class.bad]="l.variance > 0">{{ l.variance ? (l.variance | money) : '' }}</td>
          </ng-container>
          <ng-container matColumnDef="wht">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'pinv.wht' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr">@if (l.whtAmount) { {{ l.whtAmount | money }} <div class="secondary">{{ 'wht.' + l.whtCategory | t }}</div> }</td>
          </ng-container>
          <ng-container matColumnDef="amount">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.amount' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr">{{ l.amount | money }}</td>
          </ng-container>
          <tr mat-header-row *matHeaderRowDef="columns"></tr>
          <tr mat-row *matRowDef="let row; columns: columns"></tr>
        </table>
      </div>
      @if (debitNotes().length) {
        <div class="section-title">{{ 'pdn.title' | t }}</div>
        <div class="table-wrap">
          <table mat-table [dataSource]="debitNotes()">
            <ng-container matColumnDef="no">
              <th mat-header-cell *matHeaderCellDef>{{ 'pdn.no' | t }}</th>
              <td mat-cell *matCellDef="let n"><span class="ltr">{{ n.debitNoteNo }}</span> <span class="secondary ltr">{{ n.noteDate | d }}</span></td>
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
                @if (n.status === 'Posted' && auth.can('pur.invoices:post')) {
                  <button mat-icon-button (click)="reverseDebitNote(n)" [matTooltip]="'doc.reverse' | t"><mat-icon>undo</mat-icon></button>
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
  styles: `
    .header-actions { margin-block: -8px 16px; }
    .bad { color: #8c1d18; }
  `
})
export class InvoiceDetailPage implements OnInit {
  readonly id = input.required<string>();
  protected readonly auth = inject(AuthService);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  protected readonly invoiceChip = invoiceChip;
  protected readonly matchChip = matchChip;
  protected readonly columns = ['type', 'item', 'qty', 'price', 'variance', 'wht', 'amount'];
  protected readonly invoice = signal<SupplierInvoice | null>(null);
  protected readonly debitNotes = signal<DebitNote[]>([]);
  protected readonly busy = signal(false);

  ngOnInit(): void {
    this.load();
  }

  protected debitNote(i: SupplierInvoice): void {
    this.dialog
      .open(DebitNoteDialog, { data: { invoice: i }, width: '760px' })
      .afterClosed()
      .subscribe((n?: DebitNote) => n && this.load());
  }

  protected reverseDebitNote(n: DebitNote): void {
    this.dialog
      .open(ReverseDialog, { data: {} })
      .afterClosed()
      .pipe(
        filter((x): x is ReverseResult => !!x),
        switchMap((x) => this.trade.reverseDebitNote(n.id, x.date, x.reason))
      )
      .subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  private load(): void {
    this.trade.invoice(this.id()).subscribe({ next: (i) => this.invoice.set(i), error: (e) => this.notify.error(e) });
    this.trade.debitNotes({ supplierInvoiceId: this.id() }).subscribe((n) => this.debitNotes.set(n));
  }

  protected act(action: 'post' | 'approve-variance'): void {
    this.busy.set(true);
    this.trade.invoiceAction(this.id(), action).subscribe({
      next: (i) => {
        this.busy.set(false);
        this.invoice.set(i);
        this.notify.success();
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }

  protected remove(): void {
    this.dialog
      .open(ConfirmDialog, { data: { title: 'app.delete', message: 'app.delete' } })
      .afterClosed()
      .pipe(filter(Boolean), switchMap(() => this.trade.deleteInvoice(this.id())))
      .subscribe({ next: () => void this.router.navigate(['/purchasing/invoices']), error: (e) => this.notify.error(e) });
  }

  protected reverse(): void {
    this.dialog
      .open(ReverseDialog, { data: {} })
      .afterClosed()
      .pipe(
        filter((x): x is ReverseResult => !!x),
        switchMap((x) => this.trade.reverseInvoice(this.id(), x.date, x.reason))
      )
      .subscribe({ next: (i) => this.invoice.set(i), error: (e) => this.notify.error(e) });
  }
}
