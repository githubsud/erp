import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormArray, FormBuilder, FormControl, FormGroup, Validators } from '@angular/forms';
import { AuthService } from '../../core/auth.service';
import { todayIso } from '../../core/dates';
import { FinanceApi, OpeningInvoiceResult } from '../../core/finance.api';
import { NotifyService } from '../../core/notify.service';
import { SalesApi } from '../../core/sales.api';
import { TradeApi } from '../../core/trade.api';
import { SHARED } from '../../shared/shared';

type Side = 'customers' | 'suppliers';

type OpeningRow = FormGroup<{
  partyId: FormControl<string>;
  reference: FormControl<string>;
  invoiceDate: FormControl<string>;
  dueDate: FormControl<string>;
  amount: FormControl<number | null>;
}>;

interface Party {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
}

/** Cut-over: customers' and suppliers' unpaid invoices from the previous system; stock and ledger balances have their own screens. */
@Component({
  selector: 'app-opening-balances',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'ob.title' | t }}</h1></div>
    <mat-card appearance="outlined" class="form-card guide">
      <p>{{ 'ob.guide' | t }}</p>
      <ol>
        <li>{{ 'ob.step.ledger' | t }} <a routerLink="/finance/vouchers">{{ 'jv.title' | t }}</a></li>
        <li>{{ 'ob.step.stock' | t }} <a routerLink="/inventory/adjustments">{{ 'nav.adjustments' | t }}</a></li>
        <li>{{ 'ob.step.parties' | t }}</li>
        <li>{{ 'ob.step.employees' | t }}</li>
      </ol>
    </mat-card>

    <div class="toolbar-row">
      <mat-form-field>
        <mat-label>{{ 'ob.cutOver' | t }}</mat-label>
        <input matInput type="date" [formControl]="postingDate" />
        <mat-hint>{{ 'ob.cutOverHint' | t }}</mat-hint>
      </mat-form-field>
    </div>

    <mat-tab-group mat-stretch-tabs="false" (selectedIndexChange)="switchTo($event === 0 ? 'customers' : 'suppliers')">
      <mat-tab [label]="'nav.customers' | t" />
      <mat-tab [label]="'nav.suppliers' | t" />
    </mat-tab-group>

    <mat-card appearance="outlined" class="form-card">
      <table class="line-editor">
        <thead>
          <tr><th class="item-col">{{ (side() === 'customers' ? 'cust.customer' : 'po.supplier') | t }}</th><th>{{ 'ob.reference' | t }}</th>
            <th class="narrow">{{ 'ob.invoiceDate' | t }}</th><th class="narrow">{{ 'pinv.due' | t }}</th><th class="narrow">{{ 'ob.openAmount' | t }}</th><th></th></tr>
        </thead>
        <tbody>
          @for (row of rows.controls; track row; let i = $index) {
            <tr [formGroup]="row">
              <td><mat-form-field><mat-select formControlName="partyId">
                @for (p of parties(); track p.id) { <mat-option [value]="p.id"><span class="ltr">{{ p.code }}</span> · {{ p | bi }}</mat-option> }
              </mat-select></mat-form-field></td>
              <td><mat-form-field><input matInput formControlName="reference" dir="ltr" /></mat-form-field></td>
              <td class="narrow"><mat-form-field><input matInput type="date" formControlName="invoiceDate" /></mat-form-field></td>
              <td class="narrow"><mat-form-field><input matInput type="date" formControlName="dueDate" /></mat-form-field></td>
              <td class="narrow"><mat-form-field><input matInput type="number" min="0" formControlName="amount" dir="ltr" /></mat-form-field></td>
              <td><button mat-icon-button type="button" (click)="rows.removeAt(i)" [disabled]="rows.length === 1"><mat-icon>delete</mat-icon></button></td>
            </tr>
          }
          <tr class="total-row"><td colspan="4">{{ 'app.total' | t }}</td><td class="num ltr">{{ total() | money }}</td><td></td></tr>
        </tbody>
      </table>
      <div class="actions bar">
        <button mat-button type="button" (click)="addRow()"><mat-icon>add</mat-icon> {{ 'doc.addLine' | t }}</button>
        <span class="spacer"></span>
        <button mat-flat-button (click)="save()" [disabled]="rows.invalid || !postingDate.value || busy() || !canPost()">{{ 'doc.post' | t }}</button>
      </div>
    </mat-card>

    @if (posted().length) {
      <div class="section-title">{{ 'ob.posted' | t }}</div>
      <div class="table-wrap">
        <table mat-table [dataSource]="posted()">
          <ng-container matColumnDef="no">
            <th mat-header-cell *matHeaderCellDef>{{ 'sinv.no' | t }}</th>
            <td mat-cell *matCellDef="let r"><span class="ltr">{{ r.invoiceNo }}</span></td>
          </ng-container>
          <ng-container matColumnDef="party">
            <th mat-header-cell *matHeaderCellDef>{{ (side() === 'customers' ? 'cust.customer' : 'po.supplier') | t }}</th>
            <td mat-cell *matCellDef="let r">{{ partyName(r.partyId) }}</td>
          </ng-container>
          <ng-container matColumnDef="ref">
            <th mat-header-cell *matHeaderCellDef>{{ 'ob.reference' | t }}</th>
            <td mat-cell *matCellDef="let r" class="ltr">{{ r.reference }}</td>
          </ng-container>
          <ng-container matColumnDef="due">
            <th mat-header-cell *matHeaderCellDef>{{ 'pinv.due' | t }}</th>
            <td mat-cell *matCellDef="let r" class="ltr">{{ r.dueDate | d }}</td>
          </ng-container>
          <ng-container matColumnDef="amount">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'app.amount' | t }}</th>
            <td mat-cell *matCellDef="let r" class="num ltr">{{ r.amount | money }}</td>
          </ng-container>
          <tr mat-header-row *matHeaderRowDef="['no', 'party', 'ref', 'due', 'amount']"></tr>
          <tr mat-row *matRowDef="let row; columns: ['no', 'party', 'ref', 'due', 'amount']"></tr>
        </table>
      </div>
    }
  `,
  styles: `
    .guide ol { margin: 0; padding-inline-start: 20px; }
    .guide li { margin-block: 4px; }
    .bar { align-items: center; margin-top: 8px; }
    .spacer { flex: 1; }
  `
})
export class OpeningBalancesPage {
  private readonly auth = inject(AuthService);
  private readonly finance = inject(FinanceApi);
  private readonly sales = inject(SalesApi);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  private readonly fb = inject(FormBuilder).nonNullable;
  protected readonly side = signal<Side>('customers');
  protected readonly customers = signal<Party[]>([]);
  protected readonly suppliers = signal<Party[]>([]);
  protected readonly parties = computed(() => (this.side() === 'customers' ? this.customers() : this.suppliers()));
  protected readonly posted = signal<OpeningInvoiceResult[]>([]);
  protected readonly busy = signal(false);
  protected readonly postingDate = new FormControl(todayIso(), { nonNullable: true });
  protected readonly rows = new FormArray<OpeningRow>([]);
  private readonly version = signal(0);
  protected readonly total = computed(() => {
    this.version();
    return this.rows.controls.reduce((s, r) => s + (Number(r.controls.amount.value) || 0), 0);
  });
  protected readonly canPost = computed(() => this.auth.can(this.side() === 'customers' ? 'sal.invoices:post' : 'pur.invoices:post'));

  constructor() {
    this.rows.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => this.version.update((v) => v + 1));
    if (this.auth.can('sal.customers:read')) this.sales.customers().subscribe((c) => this.customers.set(c));
    if (this.auth.can('pur.suppliers:read')) this.trade.suppliers().subscribe((s) => this.suppliers.set(s));
    this.addRow();
  }

  protected switchTo(side: Side): void {
    this.side.set(side);
    this.rows.clear();
    this.posted.set([]);
    this.addRow();
  }

  protected partyName(id: string): string {
    const p = this.parties().find((x) => x.id === id);
    return p ? `${p.code} · ${p.nameAr || p.nameEn}` : '';
  }

  protected addRow(): void {
    this.rows.push(this.fb.group({
      partyId: this.fb.control('', Validators.required),
      reference: this.fb.control('', Validators.required),
      invoiceDate: this.fb.control(this.postingDate.value, Validators.required),
      dueDate: this.fb.control(''),
      amount: this.fb.control<number | null>(null, [Validators.required, Validators.min(0.01)])
    }) as OpeningRow);
  }

  protected save(): void {
    const key = this.side() === 'customers' ? 'customerId' : 'supplierId';
    const body = {
      postingDate: this.postingDate.value,
      invoices: this.rows.getRawValue().map((r) => ({
        [key]: r.partyId, reference: r.reference, invoiceDate: r.invoiceDate, dueDate: r.dueDate || null, amount: Number(r.amount)
      }))
    };
    this.busy.set(true);
    (this.side() === 'customers' ? this.finance.customerOpening(body) : this.finance.supplierOpening(body)).subscribe({
      next: (r) => {
        this.busy.set(false);
        this.posted.update((p) => [...r, ...p]);
        this.rows.clear();
        this.addRow();
        this.notify.success();
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }
}
