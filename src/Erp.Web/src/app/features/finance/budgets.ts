import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { FormArray, FormBuilder, FormControl, FormGroup, Validators } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { Router } from '@angular/router';
import { filter, switchMap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { CostCenter, FinanceApi } from '../../core/finance.api';
import { Account } from '../../core/models';
import { todayIso } from '../../core/dates';
import { NotifyService } from '../../core/notify.service';
import { ConfirmDialog } from '../../shared/dialogs';
import { SHARED } from '../../shared/shared';

interface BudgetSummary {
  id: string;
  fiscalYearName: string;
  name: string;
  status: 'Draft' | 'Approved';
  totalIncome: number;
  totalExpense: number;
}

interface BudgetRow {
  accountId: string;
  code: string;
  nameEn: string;
  nameAr: string;
  accountType: string;
  costCenterId: string | null;
  months: number[];
  total: number;
}

interface Budget {
  id: string;
  fiscalYearName: string;
  startDate: string;
  name: string;
  status: 'Draft' | 'Approved';
  rows: BudgetRow[];
  totalIncome: number;
  totalExpense: number;
}

interface VarianceRow {
  accountId: string;
  code: string;
  nameEn: string;
  nameAr: string;
  accountType: string;
  budget: number;
  actual: number;
  variance: number;
  variancePercent: number | null;
  favourable: boolean;
}

interface Variance {
  rows: VarianceRow[];
  budgetProfit: number;
  actualProfit: number;
  from: string;
  to: string;
}

@Component({
  selector: 'app-budgets',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'bud.title' | t }}</h1></div>
    @if (canManage) {
      <div class="toolbar-row">
        <mat-form-field>
          <mat-label>{{ 'bud.year' | t }}</mat-label>
          <mat-select [formControl]="yearId">
            @for (y of years(); track y.id) { <mat-option [value]="y.id">{{ y.name }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field><mat-label>{{ 'bud.name' | t }}</mat-label><input matInput [formControl]="name" /></mat-form-field>
        <button mat-flat-button (click)="create()" [disabled]="!yearId.value || !name.value"><mat-icon>add</mat-icon> {{ 'bud.new' | t }}</button>
      </div>
    }
    <table class="line-editor">
      <thead><tr><th>{{ 'bud.year' | t }}</th><th>{{ 'bud.name' | t }}</th><th class="num">{{ 'bud.income' | t }}</th><th class="num">{{ 'bud.expense' | t }}</th>
        <th class="num">{{ 'fs.netProfit' | t }}</th><th>{{ 'app.status' | t }}</th></tr></thead>
      <tbody>
        @for (b of budgets(); track b.id) {
          <tr class="clickable" [routerLink]="['/finance/budgets', b.id]">
            <td>{{ b.fiscalYearName }}</td><td>{{ b.name }}</td><td class="num ltr">{{ b.totalIncome | money }}</td><td class="num ltr">{{ b.totalExpense | money }}</td>
            <td class="num ltr">{{ b.totalIncome - b.totalExpense | money }}</td>
            <td><span class="chip" [class.ok]="b.status === 'Approved'" [class.warn]="b.status === 'Draft'">{{ 'bud.status.' + b.status | t }}</span></td>
          </tr>
        }
      </tbody>
    </table>
    @if (!budgets().length) { <p class="secondary">{{ 'app.none' | t }}</p> }
  `,
  styles: `.clickable { cursor: pointer; } .secondary { color: var(--mat-sys-on-surface-variant); }`
})
export class BudgetsPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly notify = inject(NotifyService);
  protected readonly canManage = inject(AuthService).can('fin.budgets:manage');
  protected readonly budgets = signal<BudgetSummary[]>([]);
  protected readonly years = signal<{ id: string; name: string; startDate: string; endDate: string }[]>([]);
  protected readonly yearId = new FormControl('', { nonNullable: true });
  protected readonly name = new FormControl('', { nonNullable: true });

  ngOnInit(): void {
    this.api.get<BudgetSummary[]>('finance/budgets').subscribe((b) => this.budgets.set(b));
    this.api.get<{ id: string; name: string; startDate: string; endDate: string }[]>('finance/fiscal-years').subscribe((y) => {
      this.years.set(y);
      const today = todayIso();
      this.yearId.setValue(y.find((x) => x.startDate <= today && x.endDate >= today)?.id ?? y[0]?.id ?? '');
    });
  }

  protected create(): void {
    this.api.post<Budget>('finance/budgets', { fiscalYearId: this.yearId.value, name: this.name.value }).subscribe({
      next: (b) => void this.router.navigate(['/finance/budgets', b.id]),
      error: (e) => this.notify.error(e)
    });
  }
}

type RowGroup = FormGroup<{ accountId: FormControl<string>; costCenterId: FormControl<string>; months: FormArray<FormControl<number>> }>;

@Component({
  selector: 'app-budget-detail',
  imports: [...SHARED],
  template: `
    @if (budget(); as b) {
      <div class="page-header">
        <a mat-icon-button routerLink="/finance/budgets"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
        <h1>{{ b.name }} · {{ b.fiscalYearName }}</h1>
        <span class="chip" [class.ok]="b.status === 'Approved'" [class.warn]="b.status === 'Draft'">{{ 'bud.status.' + b.status | t }}</span>
        <span class="spacer"></span>
        @if (canManage && b.status === 'Draft') {
          <button mat-stroked-button (click)="save()" [disabled]="busy()">{{ 'app.save' | t }}</button>
          <button mat-flat-button (click)="setStatus('approve')" [disabled]="busy()"><mat-icon>verified</mat-icon> {{ 'bud.approve' | t }}</button>
        }
        @if (canManage && b.status === 'Approved') { <button mat-button (click)="setStatus('reopen')">{{ 'bud.reopen' | t }}</button> }
      </div>
      <mat-tab-group mat-stretch-tabs="false" (selectedIndexChange)="$event === 1 && loadVariance()">
        <mat-tab [label]="'bud.plan' | t">
          <div class="table-wrap">
            <table class="line-editor grid">
              <thead><tr><th class="acct">{{ 'bud.account' | t }}</th><th>{{ 'fs.costCenter' | t }}</th>
                @for (m of monthLabels(); track $index) { <th class="num">{{ m }}</th> }<th class="num">{{ 'app.total' | t }}</th><th></th></tr></thead>
              <tbody>
                @for (row of rows.controls; track row; let i = $index) {
                  <tr [formGroup]="row">
                    <td class="acct"><mat-form-field><mat-select formControlName="accountId" [disabled]="!editable()">
                      @for (a of plAccounts(); track a.id) { <mat-option [value]="a.id"><span class="ltr">{{ a.code }}</span> · {{ a | bi }}</mat-option> }
                    </mat-select></mat-form-field></td>
                    <td><mat-form-field><mat-select formControlName="costCenterId">
                      <mat-option value="">—</mat-option>
                      @for (cc of costCenters(); track cc.id) { <mat-option [value]="cc.id">{{ cc.code }}</mat-option> }
                    </mat-select></mat-form-field></td>
                    @for (m of row.controls.months.controls; track $index) {
                      <td class="num"><mat-form-field><input matInput type="number" [formControl]="m" dir="ltr" [readonly]="!editable()" /></mat-form-field></td>
                    }
                    <td class="num ltr"><b>{{ rowTotal(row) | money }}</b></td>
                    <td class="nowrap">@if (editable()) {
                      <button mat-icon-button (click)="spread(row)" [matTooltip]="'bud.spread' | t"><mat-icon>calendar_view_month</mat-icon></button>
                      <button mat-icon-button (click)="rows.removeAt(i)"><mat-icon>delete</mat-icon></button> }</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          @if (editable()) { <button mat-button (click)="addRow()"><mat-icon>add</mat-icon> {{ 'bud.addAccount' | t }}</button> }
        </mat-tab>
        <mat-tab [label]="'bud.variance' | t">
          <div class="toolbar-row tab-body">
            <mat-form-field><mat-label>{{ 'app.from' | t }}</mat-label>
              <mat-select [formControl]="fromPeriod">@for (m of monthLabels(); track $index) { <mat-option [value]="$index + 1">{{ m }}</mat-option> }</mat-select></mat-form-field>
            <mat-form-field><mat-label>{{ 'app.to' | t }}</mat-label>
              <mat-select [formControl]="toPeriod">@for (m of monthLabels(); track $index) { <mat-option [value]="$index + 1">{{ m }}</mat-option> }</mat-select></mat-form-field>
            <mat-form-field><mat-label>{{ 'fs.costCenter' | t }}</mat-label>
              <mat-select [formControl]="costCenter"><mat-option value="">{{ 'fs.allCostCenters' | t }}</mat-option>
                @for (cc of costCenters(); track cc.id) { <mat-option [value]="cc.id">{{ cc.code }}</mat-option> }</mat-select></mat-form-field>
            <button mat-flat-button (click)="loadVariance()">{{ 'finance.run' | t }}</button>
          </div>
          @if (variance(); as v) {
            <table class="line-editor">
              <thead><tr><th>{{ 'bud.account' | t }}</th><th class="num">{{ 'bud.budget' | t }}</th><th class="num">{{ 'bud.actual' | t }}</th>
                <th class="num">{{ 'bud.varianceCol' | t }}</th><th class="num">%</th></tr></thead>
              <tbody>
                @for (r of v.rows; track r.accountId) {
                  <tr><td><span class="ltr">{{ r.code }}</span> · {{ r | bi }}</td><td class="num ltr">{{ r.budget | money }}</td><td class="num ltr">{{ r.actual | money }}</td>
                    <td class="num ltr" [class.good]="r.favourable" [class.bad]="!r.favourable">{{ r.variance | money }}</td>
                    <td class="num ltr">{{ r.variancePercent === null ? '—' : r.variancePercent + '%' }}</td></tr>
                }
                <tr class="total-row"><td>{{ 'fs.netProfit' | t }}</td><td class="num ltr">{{ v.budgetProfit | money }}</td><td class="num ltr">{{ v.actualProfit | money }}</td>
                  <td class="num ltr">{{ v.actualProfit - v.budgetProfit | money }}</td><td></td></tr>
              </tbody>
            </table>
          }
        </mat-tab>
      </mat-tab-group>
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: `
    .spacer { flex: 1; }
    .tab-body { margin-block-start: 16px; }
    .grid td { padding: 2px 4px; }
    .grid .num mat-form-field { width: 92px; }
    .acct { min-width: 240px; }
    .nowrap { white-space: nowrap; }
    .good { color: #0b5a35; }
    .bad { color: #b3261e; }
  `
})
export class BudgetDetailPage implements OnInit {
  readonly id = input.required<string>();
  private readonly api = inject(ApiService);
  private readonly finance = inject(FinanceApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly fb = inject(FormBuilder).nonNullable;
  protected readonly canManage = inject(AuthService).can('fin.budgets:manage');
  protected readonly budget = signal<Budget | null>(null);
  protected readonly variance = signal<Variance | null>(null);
  protected readonly accounts = signal<Account[]>([]);
  protected readonly costCenters = signal<CostCenter[]>([]);
  protected readonly busy = signal(false);
  protected readonly rows = new FormArray<RowGroup>([]);
  protected readonly fromPeriod = new FormControl(1, { nonNullable: true });
  protected readonly toPeriod = new FormControl(12, { nonNullable: true });
  protected readonly costCenter = new FormControl('', { nonNullable: true });
  protected readonly plAccounts = computed(() => this.accounts().filter((a) => a.isPostable && (a.accountType === 'Income' || a.accountType === 'Expense')));
  protected readonly editable = computed(() => this.canManage && this.budget()?.status === 'Draft');
  protected readonly monthLabels = computed(() => {
    const start = this.budget()?.startDate;
    if (!start) return [];
    const month = Number(start.slice(5, 7));
    return Array.from({ length: 12 }, (_, i) => `${((month - 1 + i) % 12) + 1}/${String(Number(start.slice(0, 4)) + Math.floor((month - 1 + i) / 12)).slice(2)}`);
  });

  ngOnInit(): void {
    this.api.accounts().subscribe((a) => this.accounts.set(a));
    this.finance.costCenters().subscribe((c) => this.costCenters.set(c.filter((x) => x.isActive)));
    this.load();
  }

  private load(): void {
    this.api.get<Budget>(`finance/budgets/${this.id()}`).subscribe({
      next: (b) => {
        this.budget.set(b);
        this.rows.clear();
        for (const r of b.rows) this.addRow(r.accountId, r.costCenterId ?? '', r.months);
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected addRow(accountId = '', costCenterId = '', months: number[] = Array(12).fill(0)): void {
    this.rows.push(this.fb.group({
      accountId: [accountId, Validators.required], costCenterId: [costCenterId],
      months: this.fb.array(months.map((m) => this.fb.control(m)))
    }) as RowGroup);
  }

  protected rowTotal(row: RowGroup): number {
    return row.controls.months.controls.reduce((s, c) => s + (Number(c.value) || 0), 0);
  }

  protected spread(row: RowGroup): void {
    const total = this.rowTotal(row);
    const answer = prompt('Yearly amount / المبلغ السنوي', String(total || ''));
    const yearly = Number(answer);
    if (!answer || Number.isNaN(yearly)) return;
    const each = Math.floor((yearly / 12) * 100) / 100;
    row.controls.months.controls.forEach((c, i) => c.setValue(i === 11 ? Math.round((yearly - each * 11) * 100) / 100 : each));
  }

  protected save(): void {
    const body = this.rows.getRawValue().filter((r) => r.accountId)
      .map((r) => ({ accountId: r.accountId, costCenterId: r.costCenterId || null, months: r.months.map((m) => Number(m) || 0) }));
    this.busy.set(true);
    this.api.put<Budget>(`finance/budgets/${this.id()}/lines`, body).subscribe({
      next: (b) => {
        this.busy.set(false);
        this.budget.set(b);
        this.notify.success();
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }

  protected setStatus(action: 'approve' | 'reopen'): void {
    this.dialog
      .open(ConfirmDialog, { data: { title: `bud.${action}`, message: `bud.${action}Confirm` } })
      .afterClosed()
      .pipe(filter(Boolean), switchMap(() => this.api.post<Budget>(`finance/budgets/${this.id()}/${action}`)))
      .subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  protected loadVariance(): void {
    this.api.get<Variance>(`finance/budgets/${this.id()}/variance`, { fromPeriod: this.fromPeriod.value, toPeriod: this.toPeriod.value, costCenterId: this.costCenter.value })
      .subscribe({ next: (v) => this.variance.set(v), error: (e) => this.notify.error(e) });
  }
}
