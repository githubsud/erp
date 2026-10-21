import { downloadCsv } from '../../shared/export';
import { CURRENCIES } from '../../core/trade.models';
import { Component, inject, input, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { Router } from '@angular/router';
import { debounceTime, distinctUntilChanged } from 'rxjs';
import { AuthService } from '../../core/auth.service';
import { todayIso } from '../../core/dates';
import { NotifyService } from '../../core/notify.service';
import { SalesApi } from '../../core/sales.api';
import { Customer, CustomerExposure, PriceList, SalesInvoice, Statement } from '../../core/sales.models';
import { NATIONALITIES, SHARED } from '../../shared/shared';
import { IMPORTS, openImport } from '../../shared/import-dialog';
import { salesInvoiceChip } from './invoices';

@Component({
  selector: 'app-customer-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ (data.customer ? 'cust.edit' : 'cust.new') | t }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field>
          <mat-label>{{ 'cust.code' | t }}</mat-label>
          <input matInput formControlName="code" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'sup.country' | t }}</mat-label>
          <mat-select formControlName="country">
            @for (c of countries; track c.code) { <mat-option [value]="c.code">{{ c.code }} · {{ c.ar }} / {{ c.en }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'sup.nameAr' | t }}</mat-label>
          <input matInput formControlName="nameAr" dir="rtl" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'sup.nameEn' | t }}</mat-label>
          <input matInput formControlName="nameEn" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'cust.creditLimit' | t }}</mat-label>
          <input matInput type="number" min="0" formControlName="creditLimit" dir="ltr" />
          <mat-hint>{{ 'cust.creditLimitHint' | t }}</mat-hint>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'sup.terms' | t }}</mat-label>
          <input matInput type="number" min="0" max="365" formControlName="paymentTermsDays" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'fx.currency' | t }}</mat-label>
          <mat-select formControlName="currency">
            @for (cur of currencies; track cur) { <mat-option [value]="cur">{{ cur }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'cust.priceList' | t }}</mat-label>
          <mat-select formControlName="priceListId">
            <mat-option [value]="null">{{ 'cust.standardPrices' | t }}</mat-option>
            @for (p of priceLists(); track p.id) { <mat-option [value]="p.id"><span class="ltr">{{ p.code }}</span> · {{ p | bi }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'sup.taxCard' | t }}</mat-label>
          <input matInput formControlName="taxCardNo" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'sup.cr' | t }}</mat-label>
          <input matInput formControlName="crNo" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'sup.phone' | t }}</mat-label>
          <input matInput formControlName="phone" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'sup.email' | t }}</mat-label>
          <input matInput type="email" formControlName="email" dir="ltr" />
        </mat-form-field>
        <mat-form-field class="full">
          <mat-label>{{ 'cust.addressAr' | t }}</mat-label>
          <input matInput formControlName="addressAr" dir="rtl" />
        </mat-form-field>
        <mat-form-field class="full">
          <mat-label>{{ 'cust.addressEn' | t }}</mat-label>
          <input matInput formControlName="addressEn" dir="ltr" />
        </mat-form-field>
        <mat-slide-toggle formControlName="isActive">{{ 'item.active' | t }}</mat-slide-toggle>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid || busy()" (click)="save()">{{ 'app.save' | t }}</button>
    </mat-dialog-actions>
  `
})
export class CustomerDialog {
  protected readonly data = inject<{ customer?: Customer }>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<CustomerDialog, Customer>);
  private readonly sales = inject(SalesApi);
  private readonly notify = inject(NotifyService);
  protected readonly countries = NATIONALITIES;
  protected readonly priceLists = signal<PriceList[]>([]);
  protected readonly busy = signal(false);
  private readonly c = this.data.customer;
  protected readonly form = inject(FormBuilder).nonNullable.group({
    code: [this.c?.code ?? '', Validators.required],
    nameEn: [this.c?.nameEn ?? ''],
    nameAr: [this.c?.nameAr ?? ''],
    country: [this.c?.country ?? 'QA'],
    creditLimit: [this.c?.creditLimit ?? (null as number | null), Validators.min(0)],
    paymentTermsDays: [this.c?.paymentTermsDays ?? 30, [Validators.min(0), Validators.max(365)]],
    priceListId: [this.c?.priceListId ?? (null as string | null)],
    taxCardNo: [this.c?.taxCardNo ?? ''],
    crNo: [this.c?.crNo ?? ''],
    phone: [this.c?.phone ?? ''],
    email: [this.c?.email ?? '', Validators.email],
    addressEn: [this.c?.addressEn ?? ''],
    addressAr: [this.c?.addressAr ?? ''],
    isActive: [this.c?.isActive ?? true],
    currency: [this.c?.currency ?? 'QAR']
  });
  protected readonly currencies = CURRENCIES;

  constructor() {
    this.sales.priceLists().subscribe((p) => this.priceLists.set(p.filter((x) => x.isActive || x.id === this.c?.priceListId)));
  }

  protected save(): void {
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.sales
      .saveCustomer(this.c?.id ?? null, {
        ...v,
        paymentTermsDays: Number(v.paymentTermsDays),
        creditLimit: v.creditLimit === null || (v.creditLimit as unknown) === '' ? null : Number(v.creditLimit),
        salespersonUserId: this.c?.salespersonUserId ?? null
      })
      .subscribe({
        next: (c) => {
          this.notify.success();
          this.ref.close(c);
        },
        error: (e) => {
          this.busy.set(false);
          this.notify.error(e);
        }
      });
  }
}

@Component({
  selector: 'app-customers',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'cust.title' | t }}</h1>
      <button mat-stroked-button (click)="exportCsv()"><mat-icon>download</mat-icon> {{ 'export.csv' | t }}</button>
      @if (auth.can('sal.customers:manage')) {
        <button mat-stroked-button (click)="importExcel()"><mat-icon>upload_file</mat-icon> {{ 'xl.button' | t }}</button>
        <a mat-stroked-button routerLink="/sales/price-lists"><mat-icon>sell</mat-icon> {{ 'nav.priceLists' | t }}</a>
        <button mat-flat-button (click)="edit()"><mat-icon>add</mat-icon> {{ 'cust.new' | t }}</button>
      }
    </div>
    <div class="toolbar-row">
      <mat-form-field>
        <mat-label>{{ 'app.search' | t }}</mat-label>
        <input matInput [formControl]="search" />
        <mat-icon matSuffix>search</mat-icon>
      </mat-form-field>
    </div>
    <div class="table-wrap">
      <table mat-table [dataSource]="rows()">
        <ng-container matColumnDef="code">
          <th mat-header-cell *matHeaderCellDef>{{ 'cust.code' | t }}</th>
          <td mat-cell *matCellDef="let c"><span class="ltr">{{ c.code }}</span></td>
        </ng-container>
        <ng-container matColumnDef="name">
          <th mat-header-cell *matHeaderCellDef>{{ 'cust.name' | t }}</th>
          <td mat-cell *matCellDef="let c">
            {{ c | bi }}
            @if (!c.isActive) { <span class="chip">{{ 'app.no' | t }} · {{ 'item.active' | t }}</span> }
          </td>
        </ng-container>
        <ng-container matColumnDef="limit">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'cust.creditLimit' | t }}</th>
          <td mat-cell *matCellDef="let c" class="num ltr">
            @if (c.creditLimit === null) { <span class="secondary">{{ 'cust.noLimit' | t }}</span> }
            @else if (c.creditLimit === 0) { <span class="chip warn">{{ 'cust.cashOnly' | t }}</span> }
            @else { {{ c.creditLimit | money }} }
          </td>
        </ng-container>
        <ng-container matColumnDef="terms">
          <th mat-header-cell *matHeaderCellDef>{{ 'sup.terms' | t }}</th>
          <td mat-cell *matCellDef="let c" class="ltr">{{ c.paymentTermsDays }}</td>
        </ng-container>
        <ng-container matColumnDef="contact">
          <th mat-header-cell *matHeaderCellDef>{{ 'sup.phone' | t }}</th>
          <td mat-cell *matCellDef="let c"><span class="ltr">{{ c.phone }}</span> <span class="secondary ltr">{{ c.email }}</span></td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns" class="clickable" (click)="router.navigate(['/sales/customers', row.id])"></tr>
      </table>
      @if (rows().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `
})
export class CustomersPage {
  protected readonly auth = inject(AuthService);
  protected readonly router = inject(Router);
  private readonly sales = inject(SalesApi);
  private readonly dialog = inject(MatDialog);
  protected readonly columns = ['code', 'name', 'limit', 'terms', 'contact'];
  protected readonly search = new FormControl('', { nonNullable: true });
  protected readonly rows = signal<Customer[]>([]);

  protected exportCsv(): void {
    downloadCsv('customers.csv', ['Code', 'Name', 'الاسم', 'Currency', 'Payment terms', 'Credit limit', 'Phone', 'Email', 'Active'], this.rows().map((x) => [x.code, x.nameEn, x.nameAr, x.currency, x.paymentTermsDays, x.creditLimit ?? '', x.phone ?? '', x.email ?? '', x.isActive]));
  }

  constructor() {
    this.search.valueChanges.pipe(debounceTime(300), distinctUntilChanged(), takeUntilDestroyed()).subscribe(() => this.load());
    this.load();
  }

  protected edit(): void {
    this.dialog
      .open(CustomerDialog, { data: {}, width: '720px' })
      .afterClosed()
      .subscribe((saved?: Customer) => saved && void this.router.navigate(['/sales/customers', saved.id]));
  }

  protected importExcel(): void {
    openImport(this.dialog, IMPORTS.customers).subscribe((done) => done && this.load());
  }

  private load(): void {
    this.sales.customers(this.search.value).subscribe((r) => this.rows.set(r));
  }
}

@Component({
  selector: 'app-customer-detail',
  imports: [...SHARED],
  template: `
    @if (customer(); as c) {
      <div class="page-header">
        <a mat-icon-button routerLink="/sales/customers"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
        <h1>{{ c | bi }} <span class="secondary ltr">{{ c.code }}</span></h1>
        @if (!c.isActive) { <span class="chip bad">{{ 'app.no' | t }} · {{ 'item.active' | t }}</span> }
      </div>
      <div class="actions header-actions">
        @if (auth.can('sal.customers:manage')) {
          <button mat-stroked-button (click)="edit(c)"><mat-icon>edit</mat-icon> {{ 'app.edit' | t }}</button>
        }
        @if (auth.can('sal.orders:manage')) {
          <a mat-stroked-button routerLink="/sales/quotations/new" [queryParams]="{ customerId: c.id }"><mat-icon>request_page</mat-icon> {{ 'quote.new' | t }}</a>
          <a mat-stroked-button routerLink="/sales/orders/new" [queryParams]="{ customerId: c.id }"><mat-icon>shopping_bag</mat-icon> {{ 'so.new' | t }}</a>
        }
        @if (auth.can('sal.invoices:post')) {
          <a mat-stroked-button routerLink="/sales/invoices/new" [queryParams]="{ customerId: c.id }"><mat-icon>receipt</mat-icon> {{ 'sinv.new' | t }}</a>
        }
        @if (auth.can('sal.receipts:post')) {
          <a mat-flat-button routerLink="/sales/receipts" [queryParams]="{ customerId: c.id }"><mat-icon>payments</mat-icon> {{ 'rcpt.new' | t }}</a>
        }
      </div>
      <div class="facts">
        @if (c.phone) { <span>{{ 'sup.phone' | t }}: <b class="ltr">{{ c.phone }}</b></span> }
        @if (c.email) { <span>{{ 'sup.email' | t }}: <b class="ltr">{{ c.email }}</b></span> }
        <span>{{ 'sup.terms' | t }}: <b class="ltr">{{ c.paymentTermsDays }}</b> {{ 'app.days' | t }}</span>
        @if (c.taxCardNo) { <span>{{ 'sup.taxCard' | t }}: <b class="ltr">{{ c.taxCardNo }}</b></span> }
        @if (c.addressAr || c.addressEn) { <span>{{ c | bi:'address' }}</span> }
      </div>

      @if (exposure(); as x) {
        <div class="card-grid">
          <mat-card appearance="outlined" class="stat"><div class="label">{{ 'cust.receivable' | t }}</div><div class="value ltr">{{ x.receivable | money }}</div></mat-card>
          <mat-card appearance="outlined" class="stat"><div class="label">{{ 'cust.chequesInHand' | t }}</div><div class="value ltr">{{ x.chequesInHand | money }}</div></mat-card>
          <mat-card appearance="outlined" class="stat"><div class="label">{{ 'cust.openOrders' | t }}</div><div class="value ltr">{{ x.openOrders | money }}</div></mat-card>
          <mat-card appearance="outlined" class="stat" [class.over]="x.available !== null && x.available < 0">
            <div class="label">{{ 'cust.available' | t }}</div>
            <div class="value ltr">{{ x.creditLimit === null ? '∞' : (x.available | money) }}</div>
            <div class="hint">{{ 'cust.creditLimit' | t }}: <span class="ltr">{{ x.creditLimit === null ? ('cust.noLimit' | t) : (x.creditLimit | money) }}</span></div>
          </mat-card>
        </div>
      }

      <mat-tab-group mat-stretch-tabs="false">
        <mat-tab [label]="'cust.openInvoices' | t">
          <div class="table-wrap tab-body">
            <table mat-table [dataSource]="invoices()">
              <ng-container matColumnDef="no">
                <th mat-header-cell *matHeaderCellDef>{{ 'sinv.no' | t }}</th>
                <td mat-cell *matCellDef="let i"><a class="ltr" [routerLink]="['/sales/invoices', i.id]">{{ i.invoiceNo }}</a></td>
              </ng-container>
              <ng-container matColumnDef="date">
                <th mat-header-cell *matHeaderCellDef>{{ 'app.date' | t }}</th>
                <td mat-cell *matCellDef="let i" class="ltr">{{ i.invoiceDate | d }}</td>
              </ng-container>
              <ng-container matColumnDef="due">
                <th mat-header-cell *matHeaderCellDef>{{ 'pinv.due' | t }}</th>
                <td mat-cell *matCellDef="let i" class="ltr" [class.overdue]="i.dueDate < today">{{ i.dueDate | d }}</td>
              </ng-container>
              <ng-container matColumnDef="total">
                <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.total' | t }}</th>
                <td mat-cell *matCellDef="let i" class="num ltr">{{ i.total | money }}</td>
              </ng-container>
              <ng-container matColumnDef="open">
                <th mat-header-cell *matHeaderCellDef class="num">{{ 'pinv.open' | t }}</th>
                <td mat-cell *matCellDef="let i" class="num ltr">{{ i.openAmount | money }}</td>
              </ng-container>
              <ng-container matColumnDef="status">
                <th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
                <td mat-cell *matCellDef="let i"><span [class]="chip(i.status)">{{ 'sinv.status.' + i.status | t }}</span></td>
              </ng-container>
              <tr mat-header-row *matHeaderRowDef="invoiceColumns"></tr>
              <tr mat-row *matRowDef="let row; columns: invoiceColumns"></tr>
            </table>
            @if (invoices().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
          </div>
        </mat-tab>
        <mat-tab [label]="'cust.statement' | t">
          <div class="toolbar-row tab-body">
            <mat-form-field><mat-label>{{ 'app.from' | t }}</mat-label><input matInput type="date" [formControl]="from" /></mat-form-field>
            <mat-form-field><mat-label>{{ 'app.to' | t }}</mat-label><input matInput type="date" [formControl]="to" /></mat-form-field>
            <button mat-flat-button (click)="loadStatement()">{{ 'finance.run' | t }}</button>
            <button mat-stroked-button (click)="statementPdf()"><mat-icon>picture_as_pdf</mat-icon> PDF</button>
          </div>
          @if (statement(); as s) {
            <div class="table-wrap">
              <table class="statement">
                <thead><tr><th>{{ 'app.date' | t }}</th><th>{{ 'cust.document' | t }}</th><th>{{ 'cust.details' | t }}</th>
                  <th class="num">{{ 'finance.debit' | t }}</th><th class="num">{{ 'finance.credit' | t }}</th><th class="num">{{ 'cust.balance' | t }}</th></tr></thead>
                <tbody>
                  <tr class="opening"><td class="ltr">{{ s.from | d }}</td><td></td><td>{{ 'cust.opening' | t }}</td><td></td><td></td>
                    <td class="num ltr">{{ s.openingBalance | money }}</td></tr>
                  @for (l of s.lines; track $index) {
                    <tr>
                      <td class="ltr">{{ l.date | d }}</td>
                      <td class="ltr">{{ l.sourceNo }}</td>
                      <td>{{ l | bi:'memo' }}</td>
                      <td class="num ltr">{{ l.debit ? (l.debit | money) : '' }}</td>
                      <td class="num ltr">{{ l.credit ? (l.credit | money) : '' }}</td>
                      <td class="num ltr">{{ l.balance | money }}</td>
                    </tr>
                  }
                  <tr class="total-row"><td colspan="5">{{ 'cust.closing' | t }}</td><td class="num ltr">{{ s.closingBalance | money }}</td></tr>
                </tbody>
              </table>
            </div>
          }
        </mat-tab>
      </mat-tab-group>
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: `
    .header-actions { margin-block: -8px 16px; }
    .tab-body { margin-block-start: 16px; }
    .stat.over { border-color: #b3261e; }
    .stat.over .value { color: #b3261e; }
    .overdue { color: #b3261e; }
    .statement { width: 100%; border-collapse: collapse; }
    .statement th, .statement td { padding: 6px 10px; border-bottom: 1px solid var(--mat-sys-outline-variant); text-align: start; }
    .statement .num { text-align: end; }
    .statement .opening td { color: var(--mat-sys-on-surface-variant); }
  `
})
export class CustomerDetailPage implements OnInit {
  readonly id = input.required<string>();
  protected readonly auth = inject(AuthService);
  private readonly sales = inject(SalesApi);
  private readonly notify = inject(NotifyService);
  private readonly dialog = inject(MatDialog);
  protected readonly chip = salesInvoiceChip;
  protected readonly today = todayIso();
  protected readonly invoiceColumns = ['no', 'date', 'due', 'total', 'open', 'status'];
  protected readonly customer = signal<Customer | null>(null);
  protected readonly exposure = signal<CustomerExposure | null>(null);
  protected readonly invoices = signal<SalesInvoice[]>([]);
  protected readonly statement = signal<Statement | null>(null);
  protected readonly from = new FormControl(`${todayIso().slice(0, 4)}-01-01`, { nonNullable: true });
  protected readonly to = new FormControl(todayIso(), { nonNullable: true });

  ngOnInit(): void {
    this.load();
    this.loadStatement();
  }

  protected edit(c: Customer): void {
    this.dialog
      .open(CustomerDialog, { data: { customer: c }, width: '720px' })
      .afterClosed()
      .subscribe((saved?: Customer) => saved && this.load());
  }

  protected loadStatement(): void {
    this.sales.statement(this.id(), this.from.value, this.to.value).subscribe({ next: (s) => this.statement.set(s), error: (e) => this.notify.error(e) });
  }

  protected statementPdf(): void {
    this.sales.statementPdf(this.id(), this.from.value, this.to.value).subscribe({ error: (e) => this.notify.error(e) });
  }

  private load(): void {
    this.sales.customer(this.id()).subscribe({ next: (c) => this.customer.set(c), error: (e) => this.notify.error(e) });
    this.sales.exposure(this.id()).subscribe((x) => this.exposure.set(x));
    this.sales.invoices({ customerId: this.id(), openOnly: true }).subscribe((i) => this.invoices.set(i));
  }
}
