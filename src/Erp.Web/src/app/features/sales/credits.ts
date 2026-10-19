import { Component, inject, input, OnInit, signal } from '@angular/core';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { filter, map, switchMap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { todayIso } from '../../core/dates';
import { Account } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

/** One credit row, normalised for customers (receipts on account, credit notes) and suppliers (debit notes). */
interface CreditRow {
  sourceType: string;
  sourceId: string;
  documentNo: string;
  date: string;
  partyId: string;
  partyNameEn: string;
  partyNameAr: string;
  original: number;
  unapplied: number;
}

interface OpenInvoice {
  id: string;
  invoiceNo: string;
  openAmount: number;
  currency: string;
}

type Side = 'customer' | 'supplier';

const CONFIG = {
  customer: {
    title: 'credit.customerTitle', hint: 'credit.customerHint', list: 'sales/credits', invoices: 'sales/invoices', apply: 'sales/credits/apply',
    refunds: 'sales/refunds', partyParam: 'customerId', permission: 'sal.receipts:post'
  },
  supplier: {
    title: 'credit.supplierTitle', hint: 'credit.supplierHint', list: 'purchasing/credits', invoices: 'purchasing/invoices', apply: 'purchasing/credits/apply',
    refunds: 'purchasing/refunds', partyParam: 'supplierId', permission: 'pur.payments:post'
  }
} as const;

@Component({
  selector: 'app-credits',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ cfg.title | t }}</h1></div>
    <p class="secondary">{{ cfg.hint | t }}</p>
    <div class="table-wrap">
      <table class="line-editor">
        <thead><tr><th>{{ 'credit.document' | t }}</th><th>{{ 'app.date' | t }}</th><th>{{ (side() === 'customer' ? 'cust.customer' : 'po.supplier') | t }}</th>
          <th class="num">{{ 'credit.original' | t }}</th><th class="num">{{ 'credit.unapplied' | t }}</th><th></th></tr></thead>
        <tbody>
          @for (r of rows(); track r.sourceId) {
            <tr>
              <td><span class="ltr">{{ r.documentNo }}</span> <span class="chip info">{{ 'credit.source.' + r.sourceType | t }}</span></td>
              <td class="ltr">{{ r.date | d }}</td>
              <td>{{ r | bi:'partyName' }}</td>
              <td class="num ltr">{{ r.original | money }}</td>
              <td class="num ltr"><b>{{ r.unapplied | money }}</b></td>
              <td class="actions-cell">
                @if (canPost) {
                  <button mat-stroked-button (click)="apply(r)">{{ 'credit.apply' | t }}</button>
                  <button mat-button (click)="refund(r)">{{ 'credit.refund' | t }}</button>
                }
              </td>
            </tr>
          }
        </tbody>
      </table>
      @if (!rows().length) { <p class="empty">{{ 'credit.none' | t }}</p> }
    </div>
  `,
  styles: `
    .secondary, .empty { color: var(--mat-sys-on-surface-variant); }
    .empty { padding: 16px; }
    .actions-cell { white-space: nowrap; text-align: end; }
  `
})
class CreditsPage implements OnInit {
  readonly side = input.required<Side>();
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly auth = inject(AuthService);
  protected cfg: (typeof CONFIG)[Side] = CONFIG.customer;
  protected canPost = false;
  protected readonly rows = signal<CreditRow[]>([]);

  ngOnInit(): void {
    this.cfg = CONFIG[this.side()];
    this.canPost = this.auth.can(this.cfg.permission);
    this.load();
  }

  private load(): void {
    const customer = this.side() === 'customer';
    this.api
      .get<Record<string, unknown>[]>(this.cfg.list)
      .pipe(
        map((list) =>
          list.map((x) =>
            customer
              ? ({ sourceType: x['sourceType'], sourceId: x['sourceId'], documentNo: x['documentNo'], date: x['date'], partyId: x['customerId'],
                  partyNameEn: x['customerNameEn'], partyNameAr: x['customerNameAr'], original: x['original'], unapplied: x['unapplied'] } as CreditRow)
              : ({ sourceType: 'DebitNote', sourceId: x['debitNoteId'], documentNo: x['debitNoteNo'], date: x['date'], partyId: x['supplierId'],
                  partyNameEn: x['supplierNameEn'], partyNameAr: x['supplierNameAr'], original: x['original'], unapplied: x['unapplied'] } as CreditRow)
          )
        )
      )
      .subscribe({ next: (r) => this.rows.set(r), error: (e) => this.notify.error(e) });
  }

  private sourceBody(r: CreditRow) {
    return this.side() === 'customer' ? { sourceType: r.sourceType, sourceId: r.sourceId } : { debitNoteId: r.sourceId };
  }

  protected apply(r: CreditRow): void {
    this.api
      .get<OpenInvoice[]>(this.cfg.invoices, { [this.cfg.partyParam]: r.partyId, openOnly: true })
      .pipe(
        switchMap((invoices) =>
          this.dialog.open(ApplyCreditDialog, { data: { credit: r, invoices: invoices.filter((i) => i.currency === 'QAR' && i.openAmount > 0) }, width: '560px' })
            .afterClosed()
        ),
        filter((a): a is { invoiceId: string; amount: number }[] => !!a?.length),
        switchMap((allocations) =>
          this.api.post(this.cfg.apply, {
            ...this.sourceBody(r), date: todayIso(),
            allocations: allocations.map((a) => (this.side() === 'customer' ? { salesInvoiceId: a.invoiceId, amount: a.amount } : { supplierInvoiceId: a.invoiceId, amount: a.amount }))
          })
        )
      )
      .subscribe({
        next: () => {
          this.notify.success();
          this.load();
        },
        error: (e) => this.notify.error(e)
      });
  }

  protected refund(r: CreditRow): void {
    this.dialog
      .open(RefundDialog, { data: { credit: r, side: this.side() }, width: '420px' })
      .afterClosed()
      .pipe(
        filter((x): x is { date: string; accountId: string; amount: number; reference: string } => !!x),
        switchMap((x) => this.api.post(this.cfg.refunds, { ...this.sourceBody(r), ...x, reference: x.reference || null }))
      )
      .subscribe({
        next: () => {
          this.notify.success();
          this.load();
        },
        error: (e) => this.notify.error(e)
      });
  }
}

@Component({ selector: 'app-customer-credits', imports: [CreditsPage], template: `<app-credits side="customer" />` })
export class CustomerCreditsPage {}

@Component({ selector: 'app-supplier-credits', imports: [CreditsPage], template: `<app-credits side="supplier" />` })
export class SupplierCreditsPage {}

@Component({
  selector: 'app-apply-credit-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'credit.apply' | t }} · <span class="ltr">{{ data.credit.documentNo }}</span></h2>
    <mat-dialog-content>
      <p class="secondary">{{ 'credit.available' | t }} <b class="ltr">{{ data.credit.unapplied | money }}</b></p>
      @if (data.invoices.length) {
        <table class="line-editor">
          <thead><tr><th>{{ 'sinv.no' | t }}</th><th class="num">{{ 'pinv.open' | t }}</th><th class="narrow">{{ 'rcpt.now' | t }}</th></tr></thead>
          <tbody>
            @for (i of data.invoices; track i.id) {
              <tr>
                <td class="ltr">{{ i.invoiceNo }}</td>
                <td class="num ltr">{{ i.openAmount | money }}</td>
                <td class="narrow"><mat-form-field><input matInput type="number" min="0" [formControl]="amounts[i.id]" dir="ltr" /></mat-form-field></td>
              </tr>
            }
          </tbody>
        </table>
      } @else {
        <p class="secondary">{{ 'credit.noInvoices' | t }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="!total() || total() > data.credit.unapplied" (click)="done()">{{ 'credit.apply' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `.secondary { color: var(--mat-sys-on-surface-variant); }`
})
export class ApplyCreditDialog {
  protected readonly data = inject<{ credit: CreditRow; invoices: OpenInvoice[] }>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<ApplyCreditDialog>);
  protected readonly amounts: Record<string, FormControl<number | null>> = {};

  constructor() {
    let left = this.data.credit.unapplied;
    for (const i of this.data.invoices) {
      const take = Math.min(left, i.openAmount);
      this.amounts[i.id] = new FormControl<number | null>(take > 0 ? take : null);
      left -= Math.max(take, 0);
    }
  }

  protected total(): number {
    return Object.values(this.amounts).reduce((s, c) => s + (Number(c.value) || 0), 0);
  }

  protected done(): void {
    this.ref.close(Object.entries(this.amounts).filter(([, c]) => Number(c.value) > 0).map(([id, c]) => ({ invoiceId: id, amount: Number(c.value) })));
  }
}

@Component({
  selector: 'app-refund-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'credit.refund' | t }} · <span class="ltr">{{ data.credit.documentNo }}</span></h2>
    <mat-dialog-content>
      <p class="secondary">{{ (data.side === 'customer' ? 'credit.refundCustomerHint' : 'credit.refundSupplierHint') | t }}</p>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field><mat-label>{{ 'app.date' | t }}</mat-label><input matInput type="date" formControlName="date" /></mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'pay.account' | t }}</mat-label>
          <mat-select formControlName="accountId">
            @for (a of accounts(); track a.id) { <mat-option [value]="a.id"><span class="ltr">{{ a.code }}</span> · {{ a | bi }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field><mat-label>{{ 'app.amount' | t }}</mat-label><input matInput type="number" min="0" formControlName="amount" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'pay.reference' | t }}</mat-label><input matInput formControlName="reference" dir="ltr" /></mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid" (click)="ref.close(form.getRawValue())">{{ 'doc.post' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `.secondary { color: var(--mat-sys-on-surface-variant); }`
})
export class RefundDialog implements OnInit {
  protected readonly data = inject<{ credit: CreditRow; side: Side }>(MAT_DIALOG_DATA);
  protected readonly ref = inject(MatDialogRef<RefundDialog>);
  private readonly api = inject(ApiService);
  protected readonly accounts = signal<Account[]>([]);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    date: [todayIso(), Validators.required],
    accountId: ['', Validators.required],
    amount: [this.data.credit.unapplied, [Validators.required, Validators.min(0.01), Validators.max(this.data.credit.unapplied)]],
    reference: ['']
  });

  ngOnInit(): void {
    this.api.accounts().subscribe((list) => {
      const usable = list.filter((a) => a.isPostable && (a.subtype === 'bank' || a.subtype === 'cash'));
      this.accounts.set(usable);
      this.form.patchValue({ accountId: usable.find((a) => a.subtype === 'bank')?.id ?? '' });
    });
  }
}
