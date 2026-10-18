import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { ActivatedRoute } from '@angular/router';
import { filter, map, merge, switchMap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { todayIso } from '../../core/dates';
import { Account } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { SalesApi } from '../../core/sales.api';
import { CreditNote, Customer, CustomerReceipt, RECEIPT_METHODS, ReceiptMethod, ReceiptStatus, SalesInvoice } from '../../core/sales.models';
import { PaymentDialog, PaymentResult, ReverseDialog, ReverseResult } from '../../shared/dialogs';
import { SHARED } from '../../shared/shared';

export const receiptChip = (s: ReceiptStatus) =>
  `chip ${s === 'Posted' || s === 'PdcCleared' ? 'ok' : s === 'PdcHeld' ? 'warn' : s === 'Bounced' ? 'bad' : ''}`;

@Component({
  selector: 'app-receipts',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'rcpt.title' | t }}</h1>
      @if (auth.can('sal.receipts:post') && !showForm()) {
        <button mat-flat-button (click)="open()"><mat-icon>add</mat-icon> {{ 'rcpt.new' | t }}</button>
      }
    </div>

    @if (showForm()) {
      <mat-card appearance="outlined" class="form-card">
        <div class="section-title">{{ 'rcpt.new' | t }}</div>
        <form [formGroup]="form" class="form-grid">
          <mat-form-field>
            <mat-label>{{ 'cust.customer' | t }}</mat-label>
            <mat-select formControlName="customerId">
              @for (c of customers(); track c.id) { <mat-option [value]="c.id"><span class="ltr">{{ c.code }}</span> · {{ c | bi }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'rcpt.method' | t }}</mat-label>
            <mat-select formControlName="method">
              @for (m of methods; track m) { <mat-option [value]="m">{{ 'rcpt.method.' + m | t }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'app.date' | t }}</mat-label>
            <input matInput type="date" formControlName="date" />
          </mat-form-field>
          @if (pdc()) {
            <mat-form-field>
              <mat-label>{{ 'rcpt.chequeDate' | t }}</mat-label>
              <input matInput type="date" formControlName="chequeDate" />
              <mat-hint>{{ 'rcpt.pdcHint' | t }}</mat-hint>
            </mat-form-field>
          } @else {
            <mat-form-field>
              <mat-label>{{ 'rcpt.account' | t }}</mat-label>
              <mat-select formControlName="depositAccountId">
                @for (a of accounts(); track a.id) { <mat-option [value]="a.id"><span class="ltr">{{ a.code }}</span> · {{ a | bi }}</mat-option> }
              </mat-select>
            </mat-form-field>
          }
          @if (cheque()) {
            <mat-form-field>
              <mat-label>{{ 'rcpt.chequeNo' | t }}</mat-label>
              <input matInput formControlName="chequeNo" dir="ltr" />
            </mat-form-field>
            <mat-form-field>
              <mat-label>{{ 'rcpt.chequeBank' | t }}</mat-label>
              <input matInput formControlName="chequeBank" />
            </mat-form-field>
          }
          <mat-form-field>
            <mat-label>{{ 'pay.reference' | t }}</mat-label>
            <input matInput formControlName="reference" dir="ltr" />
          </mat-form-field>
        </form>
        @if (openInvoices().length) {
          <table class="line-editor">
            <thead>
              <tr><th>{{ 'sinv.no' | t }}</th><th>{{ 'pinv.due' | t }}</th><th class="num">{{ 'pinv.open' | t }}</th><th class="narrow">{{ 'rcpt.now' | t }}</th></tr>
            </thead>
            <tbody>
              @for (inv of openInvoices(); track inv.id) {
                <tr>
                  <td><span class="ltr">{{ inv.invoiceNo }}</span> <span class="secondary ltr">{{ inv.customerRef }}</span></td>
                  <td class="ltr" [class.overdue]="inv.dueDate < today">{{ inv.dueDate | d }}</td>
                  <td class="num ltr">{{ inv.openAmount | money }}</td>
                  <td class="narrow"><mat-form-field><input matInput type="number" min="0" [max]="inv.openAmount" [formControl]="amounts[inv.id]" dir="ltr" /></mat-form-field></td>
                </tr>
              }
              <tr class="total-row"><td colspan="3">{{ 'po.total' | t }}</td><td class="num ltr">{{ total() | money }}</td></tr>
            </tbody>
          </table>
        } @else if (form.value.customerId) {
          <div class="empty">{{ 'rcpt.nothingOpen' | t }}</div>
        }
        <div class="actions bar">
          @if (openInvoices().length) {
            <button mat-button type="button" (click)="payAll()">{{ 'pay.payAll' | t }}</button>
            <mat-form-field class="spread">
              <mat-label>{{ 'rcpt.spread' | t }}</mat-label>
              <input matInput type="number" min="0" [formControl]="spread" dir="ltr" />
              <mat-hint>{{ 'rcpt.spreadHint' | t }}</mat-hint>
            </mat-form-field>
          }
          <span class="spacer"></span>
          <button mat-button type="button" (click)="showForm.set(false)">{{ 'app.cancel' | t }}</button>
          <button mat-flat-button (click)="save()" [disabled]="invalid() || busy()">{{ 'doc.post' | t }}</button>
        </div>
      </mat-card>
    }

    <div class="toolbar-row">
      <mat-form-field>
        <mat-label>{{ 'app.status' | t }}</mat-label>
        <mat-select [formControl]="status">
          <mat-option value="">{{ 'po.allStatuses' | t }}</mat-option>
          @for (s of statuses; track s) { <mat-option [value]="s">{{ 'rcpt.status.' + s | t }}</mat-option> }
        </mat-select>
      </mat-form-field>
    </div>
    <div class="table-wrap">
      <table mat-table [dataSource]="rows()">
        <ng-container matColumnDef="no">
          <th mat-header-cell *matHeaderCellDef>{{ 'rcpt.no' | t }}</th>
          <td mat-cell *matCellDef="let r"><span class="ltr">{{ r.receiptNo }}</span><div class="secondary ltr">{{ r.reference }}</div></td>
        </ng-container>
        <ng-container matColumnDef="date">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.date' | t }}</th>
          <td mat-cell *matCellDef="let r" class="ltr">{{ r.receiptDate | d }}</td>
        </ng-container>
        <ng-container matColumnDef="customer">
          <th mat-header-cell *matHeaderCellDef>{{ 'cust.customer' | t }}</th>
          <td mat-cell *matCellDef="let r">{{ r | bi:'customerName' }}
            <div class="secondary">@for (a of r.allocations; track a.salesInvoiceId) { <span class="ltr">{{ a.invoiceNo }}</span>&nbsp; }</div></td>
        </ng-container>
        <ng-container matColumnDef="method">
          <th mat-header-cell *matHeaderCellDef>{{ 'rcpt.method' | t }}</th>
          <td mat-cell *matCellDef="let r">{{ 'rcpt.method.' + r.method | t }}
            @if (r.chequeNo) { <div class="secondary"><span class="ltr">#{{ r.chequeNo }}</span> {{ r.chequeBank }} @if (r.chequeDate) { · <span class="ltr">{{ r.chequeDate | d }}</span> }</div> }</td>
        </ng-container>
        <ng-container matColumnDef="amount">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'app.amount' | t }}</th>
          <td mat-cell *matCellDef="let r" class="num ltr">{{ r.amount | money }}</td>
        </ng-container>
        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
          <td mat-cell *matCellDef="let r"><span [class]="chip(r.status)" [matTooltip]="r.statusReason ?? ''">{{ 'rcpt.status.' + r.status | t }}</span></td>
        </ng-container>
        <ng-container matColumnDef="actions">
          <th mat-header-cell *matHeaderCellDef></th>
          <td mat-cell *matCellDef="let r">
            @if (auth.can('sal.receipts:post')) {
              @if (r.status === 'PdcHeld') {
                <button mat-icon-button (click)="deposit(r)" [matTooltip]="'rcpt.deposit' | t" [disabled]="r.chequeDate > today"><mat-icon>account_balance</mat-icon></button>
              }
              @if (r.status === 'PdcHeld' || r.status === 'PdcCleared') {
                <button mat-icon-button (click)="bounce(r)" [matTooltip]="'rcpt.bounce' | t"><mat-icon>money_off</mat-icon></button>
              }
              @if (r.status === 'Posted' || r.status === 'PdcHeld') {
                <button mat-icon-button (click)="reverse(r)" [matTooltip]="'doc.reverse' | t"><mat-icon>undo</mat-icon></button>
              }
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
    .spread { width: 220px; }
    .overdue { color: #b3261e; }
  `
})
export class ReceiptsPage implements OnInit {
  protected readonly auth = inject(AuthService);
  private readonly sales = inject(SalesApi);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  private readonly dialog = inject(MatDialog);
  private readonly route = inject(ActivatedRoute);

  protected readonly methods = RECEIPT_METHODS;
  protected readonly statuses: ReceiptStatus[] = ['Posted', 'PdcHeld', 'PdcCleared', 'Bounced', 'Reversed'];
  protected readonly chip = receiptChip;
  protected readonly today = todayIso();
  protected readonly columns = ['no', 'date', 'customer', 'method', 'amount', 'status', 'actions'];
  protected readonly rows = signal<CustomerReceipt[]>([]);
  protected readonly customers = signal<Customer[]>([]);
  protected readonly accounts = signal<Account[]>([]);
  protected readonly openInvoices = signal<SalesInvoice[]>([]);
  protected readonly showForm = signal(false);
  protected readonly busy = signal(false);
  protected readonly status = new FormControl('', { nonNullable: true });
  protected readonly spread = new FormControl<number | null>(null);
  protected amounts: Record<string, FormControl<number | null>> = {};
  private readonly version = signal(0);
  private readonly method = signal<ReceiptMethod>('BankTransfer');
  protected readonly pdc = computed(() => this.method() === 'PostDatedCheque');
  protected readonly cheque = computed(() => this.method() === 'Cheque' || this.method() === 'PostDatedCheque');
  protected readonly form = inject(FormBuilder).nonNullable.group({
    customerId: ['', Validators.required],
    method: ['BankTransfer' as ReceiptMethod, Validators.required],
    date: [todayIso(), Validators.required],
    depositAccountId: [''],
    chequeNo: [''],
    chequeDate: [''],
    chequeBank: [''],
    reference: ['']
  });
  protected readonly total = computed(() => {
    this.version();
    return this.openInvoices().reduce((s, i) => s + (Number(this.amounts[i.id]?.value) || 0), 0);
  });

  constructor() {
    this.form.controls.customerId.valueChanges.pipe(takeUntilDestroyed()).subscribe((id) => this.loadOpen(id));
    this.form.controls.method.valueChanges.pipe(takeUntilDestroyed()).subscribe((m) => {
      this.method.set(m);
      const cash = this.accounts().find((a) => a.subtype === (m === 'Cash' ? 'cash' : 'bank'));
      if (cash) this.form.controls.depositAccountId.setValue(cash.id);
    });
    this.status.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => this.load());
    this.spread.valueChanges.pipe(takeUntilDestroyed()).subscribe((v) => this.spreadAmount(Number(v) || 0));
  }

  ngOnInit(): void {
    this.sales.customers().subscribe((c) => this.customers.set(c));
    this.api
      .accounts()
      .pipe(map((list) => list.filter((a) => a.isPostable && (a.subtype === 'bank' || a.subtype === 'cash'))))
      .subscribe((list) => {
        this.accounts.set(list);
        this.form.patchValue({ depositAccountId: list.find((a) => a.subtype === 'bank')?.id ?? '' });
      });
    this.load();
    const customerId = this.route.snapshot.queryParamMap.get('customerId');
    if (customerId && this.auth.can('sal.receipts:post')) {
      this.showForm.set(true);
      this.form.controls.customerId.setValue(customerId);
    }
  }

  protected invalid(): boolean {
    const v = this.form.getRawValue();
    if (this.form.invalid || this.total() <= 0) return true;
    if (this.pdc()) return !v.chequeNo || !v.chequeDate;
    return !v.depositAccountId || (this.cheque() && !v.chequeNo);
  }

  protected open(): void {
    this.form.patchValue({ customerId: '', date: todayIso(), reference: '', chequeNo: '', chequeDate: '', chequeBank: '' });
    this.openInvoices.set([]);
    this.showForm.set(true);
  }

  protected payAll(): void {
    for (const i of this.openInvoices()) this.amounts[i.id].setValue(i.openAmount);
  }

  protected save(): void {
    const v = this.form.getRawValue();
    const allocations = this.openInvoices()
      .filter((i) => Number(this.amounts[i.id].value) > 0)
      .map((i) => ({ salesInvoiceId: i.id, amount: Number(this.amounts[i.id].value) }));
    this.busy.set(true);
    this.sales
      .createReceipt({
        customerId: v.customerId, date: v.date, method: v.method, reference: v.reference || null,
        depositAccountId: this.pdc() ? null : v.depositAccountId,
        chequeNo: this.cheque() ? v.chequeNo : null, chequeDate: this.pdc() ? v.chequeDate : null, chequeBank: this.cheque() ? v.chequeBank || null : null,
        allocations
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

  protected deposit(r: CustomerReceipt): void {
    this.dialog
      .open(PaymentDialog, { data: { title: 'rcpt.deposit', date: r.chequeDate! > todayIso() ? r.chequeDate! : todayIso() } })
      .afterClosed()
      .pipe(
        filter((x): x is PaymentResult => !!x),
        switchMap((x) => this.sales.depositCheque(r.id, x.paymentDate, x.paymentAccountId))
      )
      .subscribe({ next: () => { this.notify.success(); this.load(); }, error: (e) => this.notify.error(e) });
  }

  protected bounce(r: CustomerReceipt): void {
    this.dialog
      .open(ReverseDialog, { data: { warning: 'rcpt.bounceHint' } })
      .afterClosed()
      .pipe(
        filter((x): x is ReverseResult => !!x),
        switchMap((x) => this.sales.bounceCheque(r.id, x.date, x.reason))
      )
      .subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  protected reverse(r: CustomerReceipt): void {
    this.dialog
      .open(ReverseDialog, { data: {} })
      .afterClosed()
      .pipe(
        filter((x): x is ReverseResult => !!x),
        switchMap((x) => this.sales.reverseReceipt(r.id, x.date, x.reason))
      )
      .subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  /** Spreads a lump sum over the open invoices, oldest due first. */
  private spreadAmount(amount: number): void {
    let left = amount;
    for (const i of this.openInvoices()) {
      const take = Math.min(left, i.openAmount);
      this.amounts[i.id].setValue(take > 0 ? Math.round(take * 100) / 100 : null);
      left -= take;
    }
  }

  private loadOpen(customerId: string): void {
    this.openInvoices.set([]);
    if (!customerId) return;
    this.sales.invoices({ customerId, openOnly: true }).subscribe((list) => {
      const sorted = [...list].sort((a, b) => a.dueDate.localeCompare(b.dueDate));
      this.amounts = Object.fromEntries(sorted.map((i) => [i.id, new FormControl<number | null>(null)]));
      merge(...Object.values(this.amounts).map((c) => c.valueChanges)).subscribe(() => this.version.update((v) => v + 1));
      this.openInvoices.set(sorted);
    });
  }

  private load(): void {
    this.sales.receipts({ status: this.status.value || undefined }).subscribe((r) => this.rows.set(r));
  }
}

@Component({
  selector: 'app-credit-notes',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'scn.title' | t }}</h1></div>
    <p class="secondary">{{ 'scn.listHint' | t }}</p>
    <div class="table-wrap">
      <table mat-table [dataSource]="rows()">
        <ng-container matColumnDef="no">
          <th mat-header-cell *matHeaderCellDef>{{ 'scn.no' | t }}</th>
          <td mat-cell *matCellDef="let n"><span class="ltr">{{ n.creditNoteNo }}</span></td>
        </ng-container>
        <ng-container matColumnDef="date">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.date' | t }}</th>
          <td mat-cell *matCellDef="let n" class="ltr">{{ n.noteDate | d }}</td>
        </ng-container>
        <ng-container matColumnDef="customer">
          <th mat-header-cell *matHeaderCellDef>{{ 'cust.customer' | t }}</th>
          <td mat-cell *matCellDef="let n">{{ n | bi:'customerName' }}</td>
        </ng-container>
        <ng-container matColumnDef="invoice">
          <th mat-header-cell *matHeaderCellDef>{{ 'sinv.no' | t }}</th>
          <td mat-cell *matCellDef="let n"><a class="ltr" [routerLink]="['/sales/invoices', n.salesInvoiceId]">{{ n.invoiceNo }}</a></td>
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
          <td mat-cell *matCellDef="let n"><button mat-icon-button (click)="pdf(n)"><mat-icon>picture_as_pdf</mat-icon></button></td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
      </table>
      @if (rows().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `
})
export class CreditNotesPage {
  private readonly sales = inject(SalesApi);
  private readonly notify = inject(NotifyService);
  protected readonly columns = ['no', 'date', 'customer', 'invoice', 'reason', 'total', 'status', 'actions'];
  protected readonly rows = signal<CreditNote[]>([]);

  constructor() {
    this.sales.creditNotes().subscribe((r) => this.rows.set(r));
  }

  protected pdf(n: CreditNote): void {
    this.sales.creditNotePdf(n.id).subscribe({ error: (e) => this.notify.error(e) });
  }
}
