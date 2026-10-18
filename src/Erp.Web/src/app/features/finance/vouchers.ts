import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormArray, FormBuilder, FormControl, FormGroup, Validators } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { filter, switchMap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { todayIso } from '../../core/dates';
import { CostCenter, FinanceApi, JournalVoucher } from '../../core/finance.api';
import { Account } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { ReverseDialog, ReverseResult } from '../../shared/dialogs';
import { SHARED } from '../../shared/shared';

type VoucherLine = FormGroup<{
  accountId: FormControl<string>;
  debit: FormControl<number | null>;
  credit: FormControl<number | null>;
  costCenterId: FormControl<string | null>;
  description: FormControl<string>;
}>;

/** Manual journal vouchers: opening balances, accruals, depreciation and corrections. Control accounts are posted through their documents. */
@Component({
  selector: 'app-vouchers',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'jv.title' | t }}</h1>
      @if (auth.can('fin.journal:post') && !showForm()) {
        <button mat-flat-button (click)="open()"><mat-icon>add</mat-icon> {{ 'jv.new' | t }}</button>
      }
    </div>

    @if (showForm()) {
      <mat-card appearance="outlined" class="form-card">
        <form [formGroup]="form" class="form-grid">
          <mat-form-field>
            <mat-label>{{ 'app.date' | t }}</mat-label>
            <input matInput type="date" formControlName="date" />
          </mat-form-field>
          <mat-form-field class="full">
            <mat-label>{{ 'jv.memoAr' | t }}</mat-label>
            <input matInput formControlName="memoAr" dir="rtl" />
          </mat-form-field>
          <mat-form-field class="full">
            <mat-label>{{ 'jv.memoEn' | t }}</mat-label>
            <input matInput formControlName="memoEn" dir="ltr" />
          </mat-form-field>
        </form>
        <p class="secondary">{{ 'jv.hint' | t }}</p>
        <table class="line-editor">
          <thead>
            <tr><th class="item-col">{{ 'finance.account' | t }}</th><th class="narrow">{{ 'finance.debit' | t }}</th><th class="narrow">{{ 'finance.credit' | t }}</th>
              <th>{{ 'jv.costCenter' | t }}</th><th>{{ 'po.description' | t }}</th><th></th></tr>
          </thead>
          <tbody>
            @for (line of lines.controls; track line; let i = $index) {
              <tr [formGroup]="line">
                <td><mat-form-field><mat-select formControlName="accountId">
                  @for (a of accounts(); track a.id) { <mat-option [value]="a.id"><span class="ltr">{{ a.code }}</span> · {{ a | bi }}</mat-option> }
                </mat-select></mat-form-field></td>
                <td class="narrow"><mat-form-field><input matInput type="number" min="0" formControlName="debit" dir="ltr" (input)="clearOther(line, 'credit')" /></mat-form-field></td>
                <td class="narrow"><mat-form-field><input matInput type="number" min="0" formControlName="credit" dir="ltr" (input)="clearOther(line, 'debit')" /></mat-form-field></td>
                <td><mat-form-field><mat-select formControlName="costCenterId">
                  <mat-option [value]="null">—</mat-option>
                  @for (c of costCenters(); track c.id) { <mat-option [value]="c.id">{{ c.code }} · {{ c | bi }}</mat-option> }
                </mat-select></mat-form-field></td>
                <td><mat-form-field><input matInput formControlName="description" /></mat-form-field></td>
                <td><button mat-icon-button type="button" (click)="lines.removeAt(i)" [disabled]="lines.length <= 2"><mat-icon>delete</mat-icon></button></td>
              </tr>
            }
            <tr class="total-row" [class.unbalanced]="difference() !== 0">
              <td>{{ 'app.total' | t }}</td><td class="num ltr">{{ totalDebit() | money }}</td><td class="num ltr">{{ totalCredit() | money }}</td>
              <td colspan="3">@if (difference() !== 0) { {{ 'jv.difference' | t }} <span class="ltr">{{ difference() | money }}</span> }</td>
            </tr>
          </tbody>
        </table>
        <div class="actions bar">
          <button mat-button type="button" (click)="addLine()"><mat-icon>add</mat-icon> {{ 'doc.addLine' | t }}</button>
          <span class="spacer"></span>
          <button mat-button type="button" (click)="showForm.set(false)">{{ 'app.cancel' | t }}</button>
          <button mat-flat-button (click)="save()" [disabled]="invalid() || busy()">{{ 'doc.post' | t }}</button>
        </div>
      </mat-card>
    }

    <div class="table-wrap">
      <table mat-table [dataSource]="rows()">
        <ng-container matColumnDef="no">
          <th mat-header-cell *matHeaderCellDef>{{ 'jv.no' | t }}</th>
          <td mat-cell *matCellDef="let v"><span class="ltr">{{ v.voucherNo }}</span></td>
        </ng-container>
        <ng-container matColumnDef="date">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.date' | t }}</th>
          <td mat-cell *matCellDef="let v" class="ltr">{{ v.voucherDate | d }}</td>
        </ng-container>
        <ng-container matColumnDef="memo">
          <th mat-header-cell *matHeaderCellDef>{{ 'jv.memo' | t }}</th>
          <td mat-cell *matCellDef="let v">{{ v | bi:'memo' }}</td>
        </ng-container>
        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
          <td mat-cell *matCellDef="let v"><span class="chip" [class.ok]="v.status === 'Posted'" [class.bad]="v.status === 'Reversed'">{{ 'doc.status.' + v.status | t }}</span></td>
        </ng-container>
        <ng-container matColumnDef="actions">
          <th mat-header-cell *matHeaderCellDef></th>
          <td mat-cell *matCellDef="let v">
            @if (v.status === 'Posted' && auth.can('fin.journal:reverse')) {
              <button mat-icon-button (click)="reverse(v)" [matTooltip]="'doc.reverse' | t"><mat-icon>undo</mat-icon></button>
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
    .unbalanced td { color: #b3261e; }
  `
})
export class VouchersPage implements OnInit {
  protected readonly auth = inject(AuthService);
  private readonly finance = inject(FinanceApi);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  private readonly dialog = inject(MatDialog);
  private readonly fb = inject(FormBuilder).nonNullable;
  protected readonly columns = ['no', 'date', 'memo', 'status', 'actions'];
  protected readonly rows = signal<JournalVoucher[]>([]);
  protected readonly accounts = signal<Account[]>([]);
  protected readonly costCenters = signal<CostCenter[]>([]);
  protected readonly showForm = signal(false);
  protected readonly busy = signal(false);
  protected readonly form = this.fb.group({ date: [todayIso(), Validators.required], memoEn: [''], memoAr: [''] });
  protected readonly lines = new FormArray<VoucherLine>([]);
  private readonly version = signal(0);
  protected readonly totalDebit = computed(() => this.sum('debit'));
  protected readonly totalCredit = computed(() => this.sum('credit'));
  protected readonly difference = computed(() => Math.round((this.totalDebit() - this.totalCredit()) * 100) / 100);

  constructor() {
    this.lines.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => this.version.update((v) => v + 1));
  }

  ngOnInit(): void {
    // Control accounts (receivables, payables, employee advances) are posted through their own documents.
    this.api.accounts().subscribe((a) => this.accounts.set(a.filter((x) => x.isPostable && !x.isControl)));
    this.finance.costCenters().subscribe((c) => this.costCenters.set(c.filter((x) => x.isActive)));
    this.load();
  }

  protected invalid(): boolean {
    return this.form.invalid || this.lines.invalid || this.totalDebit() <= 0 || this.difference() !== 0 ||
      (!this.form.value.memoEn && !this.form.value.memoAr);
  }

  protected open(): void {
    this.form.reset({ date: todayIso(), memoEn: '', memoAr: '' });
    this.lines.clear();
    this.addLine();
    this.addLine();
    this.showForm.set(true);
  }

  protected addLine(): void {
    this.lines.push(this.fb.group({
      accountId: this.fb.control('', Validators.required),
      debit: this.fb.control<number | null>(null, Validators.min(0)),
      credit: this.fb.control<number | null>(null, Validators.min(0)),
      costCenterId: this.fb.control<string | null>(null),
      description: this.fb.control('')
    }) as VoucherLine);
  }

  protected clearOther(line: VoucherLine, other: 'debit' | 'credit'): void {
    line.controls[other].setValue(null);
  }

  protected save(): void {
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.finance
      .createVoucher({
        date: v.date, memoEn: v.memoEn || null, memoAr: v.memoAr || null,
        lines: this.lines.getRawValue().map((l) => ({
          accountId: l.accountId, debit: Number(l.debit) || 0, credit: Number(l.credit) || 0, costCenterId: l.costCenterId, description: l.description || null
        }))
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

  protected reverse(v: JournalVoucher): void {
    this.dialog
      .open(ReverseDialog, { data: {} })
      .afterClosed()
      .pipe(
        filter((x): x is ReverseResult => !!x),
        switchMap((x) => this.finance.reverseVoucher(v.id, x.date, x.reason))
      )
      .subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  private sum(side: 'debit' | 'credit'): number {
    this.version();
    return Math.round(this.lines.controls.reduce((s, l) => s + (Number(l.controls[side].value) || 0), 0) * 100) / 100;
  }

  private load(): void {
    this.finance.vouchers().subscribe((r) => this.rows.set(r));
  }
}
