import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormArray, FormBuilder, FormControl, FormGroup, Validators } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { ActivatedRoute } from '@angular/router';
import { filter, map, switchMap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { todayIso } from '../../core/dates';
import { Account } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { TradeApi } from '../../core/trade.api';
import { ChargeType, GoodsReceipt, LandedCost, Supplier, SupplierInvoice, SupplierPayment } from '../../core/trade.models';
import { ConfirmDialog, ReverseDialog, ReverseResult } from '../../shared/dialogs';
import { SHARED } from '../../shared/shared';

/** Withholding the supplier will suffer on a part payment (mirrors the server rule; the server is authoritative). */
function whtOn(invoice: SupplierInvoice, paying: number): number {
  if (!invoice.whtAmount || paying <= 0) return 0;
  if (paying >= invoice.openAmount) return invoice.whtAmount - Math.round((invoice.whtAmount * invoice.paidAmount) / invoice.total * 100) / 100;
  return Math.round((invoice.whtAmount * paying) / invoice.total * 100) / 100;
}

@Component({
  selector: 'app-payments',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'pay.title' | t }}</h1>
      @if (auth.can('pur.payments:post') && !showForm()) {
        <button mat-flat-button (click)="open()"><mat-icon>add</mat-icon> {{ 'pay.new' | t }}</button>
      }
    </div>

    @if (showForm()) {
      <mat-card appearance="outlined" class="form-card">
        <div class="section-title">{{ 'pay.new' | t }}</div>
        <form [formGroup]="form" class="form-grid">
          <mat-form-field>
            <mat-label>{{ 'po.supplier' | t }}</mat-label>
            <mat-select formControlName="supplierId">
              @for (s of suppliers(); track s.id) { <mat-option [value]="s.id"><span class="ltr">{{ s.code }}</span> · {{ s | bi }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'pay.account' | t }}</mat-label>
            <mat-select formControlName="paymentAccountId">
              @for (a of accounts(); track a.id) { <mat-option [value]="a.id"><span class="ltr">{{ a.code }}</span> · {{ a | bi }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'app.date' | t }}</mat-label>
            <input matInput type="date" formControlName="date" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'pay.reference' | t }}</mat-label>
            <input matInput formControlName="reference" dir="ltr" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'pdc.chequeNo' | t }}</mat-label>
            <input matInput formControlName="chequeNo" dir="ltr" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'pdc.chequeDate' | t }}</mat-label>
            <input matInput type="date" formControlName="chequeDate" />
            <mat-hint>{{ 'pdc.hint' | t }}</mat-hint>
          </mat-form-field>
          @if (currency() !== 'QAR') {
            <mat-form-field>
              <mat-label>{{ 'fx.rate' | t: { c: currency() } }}</mat-label>
              <input matInput type="number" min="0" step="0.0001" [formControl]="rate" dir="ltr" />
              <mat-hint>{{ 'fx.paymentRateHint' | t }}</mat-hint>
            </mat-form-field>
          }
        </form>
        @if (mixedCurrencies()) { <div class="error-panel">{{ 'fx.mixed' | t }}</div> }
        @if (openInvoices().length) {
          <table class="line-editor">
            <thead>
              <tr><th>{{ 'pinv.no' | t }}</th><th>{{ 'pinv.due' | t }}</th><th class="num">{{ 'pinv.open' | t }}</th><th class="narrow">{{ 'pay.now' | t }}</th>
                <th class="num">{{ 'pay.wht' | t }}</th></tr>
            </thead>
            <tbody>
              @for (inv of openInvoices(); track inv.id) {
                <tr>
                  <td><span class="ltr">{{ inv.invoiceNo }}</span> <span class="secondary ltr">{{ inv.supplierRef }}</span>
                    @if (inv.currency !== 'QAR') { <span class="chip info">{{ inv.currency }}</span> }</td>
                  <td class="ltr">{{ inv.dueDate | d }}</td>
                  <td class="num ltr">{{ inv.openAmount | money }}</td>
                  <td class="narrow"><mat-form-field><input matInput type="number" min="0" [max]="inv.openAmount" [formControl]="amounts[inv.id]" dir="ltr" /></mat-form-field></td>
                  <td class="num ltr">{{ wht(inv) | money }}</td>
                </tr>
              }
              <tr class="total-row">
                <td colspan="3">{{ 'pay.gross' | t }} <span class="ltr">{{ gross() | money }}</span> · {{ 'pay.wht' | t }} <span class="ltr">{{ whtTotal() | money }}</span></td>
                <td colspan="2">{{ 'pay.net' | t }} <span class="ltr">{{ gross() - whtTotal() | money }}</span></td>
              </tr>
            </tbody>
          </table>
        } @else if (form.value.supplierId) {
          <div class="empty">{{ 'app.none' | t }}</div>
        }
        <div class="actions bar">
          @if (openInvoices().length) { <button mat-button type="button" (click)="payAll()">{{ 'pay.payAll' | t }}</button> }
          <span class="spacer"></span>
          <button mat-button type="button" (click)="showForm.set(false)">{{ 'app.cancel' | t }}</button>
          <button mat-flat-button (click)="save()"
            [disabled]="form.invalid || gross() <= 0 || busy() || mixedCurrencies() || (currency() !== 'QAR' && !(Number(rate.value) > 0))">{{ 'doc.post' | t }}</button>
        </div>
      </mat-card>
    }

    <div class="table-wrap">
      <table mat-table [dataSource]="rows()">
        <ng-container matColumnDef="no">
          <th mat-header-cell *matHeaderCellDef>{{ 'pay.no' | t }}</th>
          <td mat-cell *matCellDef="let p"><span class="ltr">{{ p.paymentNo }}</span><div class="secondary ltr">{{ p.reference }}</div></td>
        </ng-container>
        <ng-container matColumnDef="date">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.date' | t }}</th>
          <td mat-cell *matCellDef="let p" class="ltr">{{ p.paymentDate | d }}</td>
        </ng-container>
        <ng-container matColumnDef="supplier">
          <th mat-header-cell *matHeaderCellDef>{{ 'po.supplier' | t }}</th>
          <td mat-cell *matCellDef="let p">{{ p | bi:'supplierName' }}
            <div class="secondary">@for (a of p.allocations; track a.supplierInvoiceId) { <span class="ltr">{{ a.supplierRef }}</span>&nbsp; }</div></td>
        </ng-container>
        <ng-container matColumnDef="gross">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'pay.gross' | t }}</th>
          <td mat-cell *matCellDef="let p" class="num ltr">@if (p.currency !== 'QAR') { <span class="secondary">{{ p.currency }} @ {{ p.exchangeRate }}</span> }
            {{ p.grossAmount | money }}
            @if (p.fxDifference) { <div class="secondary" [class.loss]="p.fxDifference < 0">{{ 'fx.difference' | t }} {{ p.fxDifference | money }}</div> }</td>
        </ng-container>
        <ng-container matColumnDef="wht">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'pay.wht' | t }}</th>
          <td mat-cell *matCellDef="let p" class="num ltr">{{ p.whtAmount ? (p.whtAmount | money) : '' }}</td>
        </ng-container>
        <ng-container matColumnDef="net">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'pay.net' | t }}</th>
          <td mat-cell *matCellDef="let p" class="num ltr">{{ p.netAmount | money }}</td>
        </ng-container>
        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
          <td mat-cell *matCellDef="let p"><span class="chip" [class.ok]="p.status === 'Posted'">{{ 'doc.status.' + p.status | t }}</span>
            @if (p.pdcStatus && p.status === 'Posted') {
              <div><span class="chip" [class.warn]="p.pdcStatus === 'Issued'" [class.ok]="p.pdcStatus === 'Cleared'">{{ 'pdc.status.' + p.pdcStatus | t }}</span>
                <span class="secondary ltr">{{ p.chequeNo }} · {{ p.chequeDate | d }}</span></div>
            }</td>
        </ng-container>
        <ng-container matColumnDef="actions">
          <th mat-header-cell *matHeaderCellDef></th>
          <td mat-cell *matCellDef="let p">
            @if (p.status === 'Posted' && p.pdcStatus === 'Issued' && auth.can('pur.payments:post')) {
              <button mat-icon-button (click)="clear(p)" [matTooltip]="'pdc.clear' | t"><mat-icon>price_check</mat-icon></button>
            }
            @if (p.status === 'Posted' && auth.can('pur.payments:post')) {
              <button mat-icon-button (click)="reverse(p)" [matTooltip]="'doc.reverse' | t"><mat-icon>undo</mat-icon></button>
            }
          </td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
      </table>
      @if (rows().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `,
  styles: `
    .bar { align-items: center; margin-top: 8px; }
    .spacer { flex: 1; }
    .loss { color: #b3261e; }
  `
})
export class PaymentsPage implements OnInit {
  protected readonly auth = inject(AuthService);
  private readonly trade = inject(TradeApi);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  private readonly dialog = inject(MatDialog);
  private readonly route = inject(ActivatedRoute);

  protected readonly columns = ['no', 'date', 'supplier', 'gross', 'wht', 'net', 'status', 'actions'];
  protected readonly rows = signal<SupplierPayment[]>([]);
  protected readonly suppliers = signal<Supplier[]>([]);
  protected readonly accounts = signal<Account[]>([]);
  protected readonly openInvoices = signal<SupplierInvoice[]>([]);
  protected readonly showForm = signal(false);
  protected readonly busy = signal(false);
  protected amounts: Record<string, FormControl<number | null>> = {};
  protected readonly rate = new FormControl<number | null>(null);
  protected readonly Number = Number;
  private readonly version = signal(0);
  /** The currency of the invoices being paid (all must share one). */
  protected readonly currency = computed(() => {
    this.version();
    const paying = this.openInvoices().filter((i) => Number(this.amounts[i.id]?.value) > 0);
    return (paying[0] ?? this.openInvoices()[0])?.currency ?? 'QAR';
  });
  protected readonly mixedCurrencies = computed(() => {
    this.version();
    return new Set(this.openInvoices().filter((i) => Number(this.amounts[i.id]?.value) > 0).map((i) => i.currency)).size > 1;
  });
  protected readonly form = inject(FormBuilder).nonNullable.group({
    supplierId: ['', Validators.required],
    paymentAccountId: ['', Validators.required],
    date: [todayIso(), Validators.required],
    reference: [''],
    chequeNo: [''],
    chequeDate: ['']
  });
  protected readonly gross = computed(() => {
    this.version();
    return this.openInvoices().reduce((s, i) => s + (Number(this.amounts[i.id]?.value) || 0), 0);
  });
  protected readonly whtTotal = computed(() => {
    this.version();
    return this.openInvoices().reduce((s, i) => s + this.wht(i), 0);
  });

  constructor() {
    this.form.controls.supplierId.valueChanges.pipe(takeUntilDestroyed()).subscribe((id) => this.loadOpen(id));
  }

  ngOnInit(): void {
    this.trade.suppliers().subscribe((s) => this.suppliers.set(s));
    this.api
      .accounts()
      .pipe(map((list) => list.filter((a) => a.isPostable && (a.subtype === 'bank' || a.subtype === 'cash'))))
      .subscribe((list) => {
        this.accounts.set(list);
        this.form.patchValue({ paymentAccountId: list.find((a) => a.subtype === 'bank')?.id ?? '' });
      });
    this.load();
    const supplierId = this.route.snapshot.queryParamMap.get('supplierId');
    if (supplierId && this.auth.can('pur.payments:post')) {
      this.showForm.set(true);
      this.form.controls.supplierId.setValue(supplierId);
    }
  }

  protected wht(invoice: SupplierInvoice): number {
    return whtOn(invoice, Number(this.amounts[invoice.id]?.value) || 0);
  }

  protected open(): void {
    this.form.patchValue({ supplierId: '', date: todayIso(), reference: '', chequeNo: '', chequeDate: '' });
    this.openInvoices.set([]);
    this.showForm.set(true);
  }

  protected payAll(): void {
    for (const i of this.openInvoices()) this.amounts[i.id].setValue(i.openAmount);
  }

  protected save(): void {
    const allocations = this.openInvoices()
      .filter((i) => Number(this.amounts[i.id].value) > 0)
      .map((i) => ({ supplierInvoiceId: i.id, amount: Number(this.amounts[i.id].value) }));
    this.busy.set(true);
    const v = this.form.getRawValue();
    this.trade.createPayment({ ...v, chequeNo: v.chequeNo || null, chequeDate: v.chequeDate || null, allocations,
      exchangeRate: this.currency() === 'QAR' ? null : Number(this.rate.value) }).subscribe({
      next: () => {
        this.busy.set(false);
        this.showForm.set(false);
        this.notify.success();
        this.load();
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }

  /** The bank paid the cheque on (or after) its date. */
  protected clear(p: SupplierPayment): void {
    const today = todayIso();
    const date = p.chequeDate && p.chequeDate > today ? p.chequeDate : today;
    this.dialog
      .open(ConfirmDialog, { data: { title: 'pdc.clear', message: 'pdc.clearConfirm' } })
      .afterClosed()
      .pipe(filter(Boolean), switchMap(() => this.trade.clearPayment(p.id, date)))
      .subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  protected reverse(p: SupplierPayment): void {
    this.dialog
      .open(ReverseDialog, { data: {} })
      .afterClosed()
      .pipe(
        filter((x): x is ReverseResult => !!x),
        switchMap((x) => this.trade.reversePayment(p.id, x.date, x.reason))
      )
      .subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  private loadOpen(supplierId: string): void {
    this.openInvoices.set([]);
    if (!supplierId) return;
    this.trade.invoices({ supplierId, openOnly: true }).subscribe((list) => {
      const sorted = [...list].sort((a, b) => a.dueDate.localeCompare(b.dueDate));
      this.amounts = Object.fromEntries(sorted.map((i) => [i.id, new FormControl<number | null>(null)]));
      for (const c of Object.values(this.amounts)) c.valueChanges.subscribe(() => this.version.update((v) => v + 1));
      this.openInvoices.set(sorted);
    });
  }

  private load(): void {
    this.trade.payments().subscribe((r) => this.rows.set(r));
  }
}

type ChargeGroup = FormGroup<{
  chargeType: FormControl<ChargeType>;
  description: FormControl<string>;
  amount: FormControl<number | null>;
  supplierInvoiceId: FormControl<string | null>;
}>;

@Component({
  selector: 'app-landed-costs',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'lcv.title' | t }}</h1>
      @if (auth.can('pur.invoices:post') && !showForm()) {
        <button mat-flat-button (click)="open()"><mat-icon>add</mat-icon> {{ 'lcv.new' | t }}</button>
      }
    </div>

    @if (showForm()) {
      <mat-card appearance="outlined" class="form-card">
        <div class="section-title">{{ 'lcv.new' | t }}</div>
        <p class="secondary">{{ 'lcv.hint' | t }}</p>
        <form [formGroup]="form" class="form-grid">
          <mat-form-field>
            <mat-label>{{ 'app.date' | t }}</mat-label>
            <input matInput type="date" formControlName="date" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'lcv.method' | t }}</mat-label>
            <mat-select formControlName="method">
              <mat-option value="Value">{{ 'lcv.method.Value' | t }}</mat-option>
              <mat-option value="Quantity">{{ 'lcv.method.Quantity' | t }}</mat-option>
            </mat-select>
          </mat-form-field>
          <mat-form-field class="full">
            <mat-label>{{ 'lcv.receipts' | t }}</mat-label>
            <mat-select formControlName="goodsReceiptIds" multiple>
              @for (r of receipts(); track r.id) {
                <mat-option [value]="r.id"><span class="ltr">{{ r.grnNo }}</span> · {{ r | bi:'supplierName' }} · <span class="ltr">{{ r.totalValue | money }}</span></mat-option>
              }
            </mat-select>
          </mat-form-field>
          <mat-form-field class="full">
            <mat-label>{{ 'app.notes' | t }}</mat-label>
            <input matInput formControlName="notes" />
          </mat-form-field>
        </form>
        <div class="section-title">{{ 'lcv.charges' | t }}</div>
        <table class="line-editor">
          <thead><tr><th>{{ 'lcv.chargeType' | t }}</th><th>{{ 'lcv.invoice' | t }}</th><th>{{ 'po.description' | t }}</th><th class="narrow">{{ 'app.amount' | t }}</th><th></th></tr></thead>
          <tbody>
            @for (c of charges.controls; track c; let i = $index) {
              <tr [formGroup]="c">
                <td><mat-form-field><mat-select formControlName="chargeType">
                  @for (t of chargeTypes; track t) { <mat-option [value]="t">{{ 'lcv.charge.' + t | t }}</mat-option> }
                </mat-select></mat-form-field></td>
                <td><mat-form-field><mat-select formControlName="supplierInvoiceId">
                  <mat-option [value]="null">—</mat-option>
                  @for (inv of chargeInvoices(); track inv.id) {
                    <mat-option [value]="inv.id"><span class="ltr">{{ inv.invoiceNo }}</span> · {{ inv | bi:'supplierName' }}</mat-option>
                  }
                </mat-select></mat-form-field></td>
                <td><mat-form-field><input matInput formControlName="description" /></mat-form-field></td>
                <td class="narrow"><mat-form-field><input matInput type="number" min="0" formControlName="amount" dir="ltr" /></mat-form-field></td>
                <td><button mat-icon-button type="button" (click)="charges.removeAt(i)" [disabled]="charges.length === 1"><mat-icon>delete</mat-icon></button></td>
              </tr>
            }
          </tbody>
        </table>
        <div class="actions bar">
          <button mat-button type="button" (click)="addCharge()"><mat-icon>add</mat-icon> {{ 'lcv.addCharge' | t }}</button>
          <span class="spacer"></span>
          <button mat-button type="button" (click)="showForm.set(false)">{{ 'app.cancel' | t }}</button>
          <button mat-flat-button (click)="save()" [disabled]="form.invalid || charges.invalid || busy()">{{ 'doc.post' | t }}</button>
        </div>
      </mat-card>
    }

    @for (v of rows(); track v.id) {
      <mat-card appearance="outlined" class="form-card voucher">
        <div class="voucher-head">
          <b class="ltr">{{ v.voucherNo }}</b>
          <span class="ltr">{{ v.voucherDate | d }}</span>
          <span>{{ 'lcv.method' | t }}: {{ 'lcv.method.' + v.method | t }}</span>
          <span class="chip" [class.ok]="v.status === 'Posted'">{{ 'doc.status.' + v.status | t }}</span>
          <span class="spacer"></span>
          <b class="ltr">{{ v.totalAmount | money }}</b>
          @if (v.status === 'Posted' && auth.can('pur.invoices:post')) {
            <button mat-icon-button (click)="reverse(v)" [matTooltip]="'doc.reverse' | t"><mat-icon>undo</mat-icon></button>
          }
        </div>
        <div class="secondary">@for (c of v.charges; track c.lineNo) { {{ 'lcv.charge.' + c.chargeType | t }} <span class="ltr">{{ c.amount | money }}</span>&nbsp;&nbsp; }</div>
        <table class="alloc">
          <thead><tr><th>{{ 'po.item' | t }}</th><th class="num">{{ 'lcv.allocation' | t }}</th><th class="num">{{ 'lcv.toInventory' | t }}</th><th class="num">{{ 'lcv.toCogs' | t }}</th></tr></thead>
          <tbody>
            @for (a of v.allocations; track a.goodsReceiptLineId) {
              <tr><td class="ltr">{{ a.sku }}</td><td class="num ltr">{{ a.amount | money }}</td><td class="num ltr">{{ a.toInventory | money }}</td><td class="num ltr">{{ a.toCostOfSales | money }}</td></tr>
            }
          </tbody>
        </table>
      </mat-card>
    }
    @if (rows().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
  `,
  styles: `
    .bar { align-items: center; margin-top: 8px; }
    .spacer { flex: 1; }
    .voucher-head { display: flex; gap: 16px; align-items: center; flex-wrap: wrap; padding-top: 8px; }
    .alloc { width: 100%; border-collapse: collapse; margin-top: 8px; }
    .alloc th, .alloc td { padding: 4px 8px; border-bottom: 1px solid var(--mat-sys-outline-variant); text-align: start; }
    .alloc .num { text-align: end; }
  `
})
export class LandedCostsPage implements OnInit {
  protected readonly auth = inject(AuthService);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  private readonly dialog = inject(MatDialog);
  private readonly fb = inject(FormBuilder).nonNullable;

  protected readonly chargeTypes: ChargeType[] = ['Freight', 'Clearing', 'CustomsDuty', 'Insurance', 'Other'];
  protected readonly rows = signal<LandedCost[]>([]);
  protected readonly receipts = signal<GoodsReceipt[]>([]);
  protected readonly chargeInvoices = signal<SupplierInvoice[]>([]);
  protected readonly showForm = signal(false);
  protected readonly busy = signal(false);
  protected readonly form = this.fb.group({
    date: [todayIso(), Validators.required],
    method: ['Value' as 'Value' | 'Quantity', Validators.required],
    goodsReceiptIds: this.fb.control<string[]>([], Validators.required),
    notes: ['']
  });
  protected readonly charges = new FormArray<ChargeGroup>([]);

  ngOnInit(): void {
    this.load();
  }

  protected open(): void {
    this.trade.receipts().subscribe((r) => this.receipts.set(r.filter((x) => x.status === 'Posted')));
    this.trade
      .invoices()
      .subscribe((list) =>
        this.chargeInvoices.set(list.filter((i) => i.status !== 'Draft' && i.status !== 'Reversed' && i.lines.some((l) => l.lineType === 'LandedCost')))
      );
    this.form.reset({ date: todayIso(), method: 'Value', goodsReceiptIds: [], notes: '' });
    this.charges.clear();
    this.addCharge();
    this.showForm.set(true);
  }

  protected addCharge(): void {
    const group = this.fb.group({
      chargeType: this.fb.control<ChargeType>('Freight'),
      description: this.fb.control(''),
      amount: this.fb.control<number | null>(null, [Validators.required, Validators.min(0.01)]),
      supplierInvoiceId: this.fb.control<string | null>(null)
    }) as ChargeGroup;
    // Picking the freight/customs invoice fills in its landed cost amount.
    group.controls.supplierInvoiceId.valueChanges.subscribe((id) => {
      const inv = this.chargeInvoices().find((i) => i.id === id);
      if (inv && group.controls.amount.value === null)
        group.controls.amount.setValue(inv.lines.filter((l) => l.lineType === 'LandedCost').reduce((s, l) => s + l.amount, 0));
    });
    this.charges.push(group);
  }

  protected save(): void {
    this.busy.set(true);
    this.trade
      .createLandedCost({
        ...this.form.getRawValue(),
        charges: this.charges.getRawValue().map((c) => ({ ...c, amount: Number(c.amount), description: c.description || null }))
      })
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.showForm.set(false);
          this.notify.success();
          this.load();
        },
        error: (e) => {
          this.busy.set(false);
          this.notify.error(e);
        }
      });
  }

  protected reverse(v: LandedCost): void {
    this.dialog
      .open(ReverseDialog, { data: {} })
      .afterClosed()
      .pipe(
        filter((x): x is ReverseResult => !!x),
        switchMap((x) => this.trade.reverseLandedCost(v.id, x.date, x.reason))
      )
      .subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  private load(): void {
    this.trade.landedCosts().subscribe((r) => this.rows.set(r));
  }
}
