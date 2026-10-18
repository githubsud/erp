import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { Router } from '@angular/router';
import { filter, switchMap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { todayIso } from '../../core/dates';
import { BankLine, BankReconciliation, BankStatement, CostCenter, FinanceApi, ImportResult, LedgerItem } from '../../core/finance.api';
import { Account } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { ConfirmDialog } from '../../shared/dialogs';
import { SHARED } from '../../shared/shared';

const lineChip = (s: BankLine['status']) => `chip ${s === 'Matched' ? 'ok' : s === 'Ignored' ? '' : 'warn'}`;

@Component({
  selector: 'app-bank',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'bank.title' | t }}</h1></div>
    <div class="toolbar-row">
      <mat-form-field>
        <mat-label>{{ 'bank.account' | t }}</mat-label>
        <mat-select [formControl]="accountId" (selectionChange)="load()">
          @for (a of accounts(); track a.id) { <mat-option [value]="a.id"><span class="ltr">{{ a.code }}</span> · {{ a | bi }}</mat-option> }
        </mat-select>
      </mat-form-field>
      <mat-form-field>
        <mat-label>{{ 'stock.asOf' | t }}</mat-label>
        <input matInput type="date" [formControl]="asOf" (change)="loadReconciliation()" />
      </mat-form-field>
    </div>

    @if (rec(); as r) {
      <div class="card-grid">
        <mat-card appearance="outlined" class="stat"><div class="label">{{ 'bank.ledgerBalance' | t }}</div><div class="value ltr">{{ r.ledgerBalance | money }}</div>
          <div class="hint">{{ 'bank.adjusted' | t }} <span class="ltr">{{ r.adjustedLedgerBalance | money }}</span></div></mat-card>
        <mat-card appearance="outlined" class="stat"><div class="label">{{ 'bank.statementBalance' | t }}</div>
          <div class="value ltr">{{ r.statementBalance === null ? '—' : (r.statementBalance | money) }}</div>
          <div class="hint">@if (r.statementDate) { <span class="ltr">{{ r.statementDate | d }}</span> · } {{ 'bank.adjusted' | t }}
            <span class="ltr">{{ r.adjustedStatementBalance === null ? '—' : (r.adjustedStatementBalance | money) }}</span></div></mat-card>
        <mat-card appearance="outlined" class="stat" [class.bad]="r.difference !== null && r.difference !== 0" [class.good]="r.difference === 0">
          <div class="label">{{ 'stock.difference' | t }}</div><div class="value ltr">{{ r.difference === null ? '—' : (r.difference | money) }}</div>
          <div class="hint">{{ (r.difference === 0 ? 'bank.reconciled' : 'bank.differenceHint') | t }}</div></mat-card>
      </div>
      <div class="two-col">
        <div>
          <div class="section-title">{{ 'bank.uncleared' | t }} <span class="secondary">({{ r.unclearedLedgerItems.length }})</span></div>
          <p class="secondary">{{ 'bank.unclearedHint' | t }} @if (r.reconcileFrom) { {{ 'bank.since' | t: { d: (r.reconcileFrom | d) } }} }</p>
          <div class="table-wrap compact">
            <table mat-table [dataSource]="r.unclearedLedgerItems">
              <ng-container matColumnDef="date"><th mat-header-cell *matHeaderCellDef>{{ 'app.date' | t }}</th>
                <td mat-cell *matCellDef="let i" class="ltr">{{ i.postingDate | d }}</td></ng-container>
              <ng-container matColumnDef="doc"><th mat-header-cell *matHeaderCellDef>{{ 'cust.document' | t }}</th>
                <td mat-cell *matCellDef="let i"><span class="ltr">{{ i.sourceNo }}</span><div class="secondary">{{ i.memo }}</div></td></ng-container>
              <ng-container matColumnDef="amount"><th mat-header-cell *matHeaderCellDef class="num">{{ 'app.amount' | t }}</th>
                <td mat-cell *matCellDef="let i" class="num ltr" [class.out]="i.amount < 0">{{ i.amount | money }}</td></ng-container>
              <tr mat-header-row *matHeaderRowDef="['date', 'doc', 'amount']"></tr>
              <tr mat-row *matRowDef="let row; columns: ['date', 'doc', 'amount']"></tr>
            </table>
            @if (r.unclearedLedgerItems.length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
          </div>
        </div>
        <div>
          <div class="section-title">{{ 'bank.unrecorded' | t }} <span class="secondary">({{ r.unrecordedBankItems.length }})</span></div>
          <p class="secondary">{{ 'bank.unrecordedHint' | t }}</p>
          <div class="table-wrap compact">
            <table mat-table [dataSource]="r.unrecordedBankItems">
              <ng-container matColumnDef="date"><th mat-header-cell *matHeaderCellDef>{{ 'app.date' | t }}</th>
                <td mat-cell *matCellDef="let l" class="ltr">{{ l.date | d }}</td></ng-container>
              <ng-container matColumnDef="desc"><th mat-header-cell *matHeaderCellDef>{{ 'cust.details' | t }}</th>
                <td mat-cell *matCellDef="let l">{{ l.description }}<div class="secondary ltr">{{ l.reference }}</div></td></ng-container>
              <ng-container matColumnDef="amount"><th mat-header-cell *matHeaderCellDef class="num">{{ 'app.amount' | t }}</th>
                <td mat-cell *matCellDef="let l" class="num ltr" [class.out]="l.amount < 0">{{ l.amount | money }}</td></ng-container>
              <tr mat-header-row *matHeaderRowDef="['date', 'desc', 'amount']"></tr>
              <tr mat-row *matRowDef="let row; columns: ['date', 'desc', 'amount']"></tr>
            </table>
            @if (r.unrecordedBankItems.length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
          </div>
        </div>
      </div>
    }

    <mat-card appearance="outlined" class="form-card import">
      <div class="section-title">{{ 'bank.import' | t }}</div>
      <p class="secondary">{{ 'bank.importHint' | t }}</p>
      <div class="actions">
        <button mat-stroked-button (click)="template()"><mat-icon>download</mat-icon> {{ 'import.template' | t }}</button>
        <input #picker type="file" accept=".xlsx,.csv,.txt" hidden (change)="file.set(picker.files?.item(0) ?? null); picker.value = ''" />
        <button mat-stroked-button (click)="picker.click()"><mat-icon>upload_file</mat-icon> {{ 'import.choose' | t }}</button>
        @if (file()) { <span class="ltr">{{ file()!.name }}</span> }
        <mat-form-field class="closing">
          <mat-label>{{ 'bank.closingBalance' | t }}</mat-label>
          <input matInput type="number" [formControl]="closing" dir="ltr" />
          <mat-hint>{{ 'bank.closingHint' | t }}</mat-hint>
        </mat-form-field>
        <span class="spacer"></span>
        <button mat-flat-button (click)="import()" [disabled]="!file() || !accountId.value || busy()"><mat-icon>cloud_upload</mat-icon> {{ 'import.import' | t }}</button>
      </div>
      @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
      @if (result(); as res) {
        <div class="result">{{ 'bank.imported' | t: { n: res.lines, m: res.autoMatched } }}</div>
        @for (e of res.errors; track e) { <div class="error-panel">{{ e }}</div> }
      }
    </mat-card>

    <div class="section-title">{{ 'bank.statements' | t }}</div>
    <div class="table-wrap">
      <table mat-table [dataSource]="statements()">
        <ng-container matColumnDef="no"><th mat-header-cell *matHeaderCellDef>{{ 'bank.statementNo' | t }}</th>
          <td mat-cell *matCellDef="let s"><span class="ltr">{{ s.statementNo }}</span><div class="secondary ltr">{{ s.fileName }}</div></td></ng-container>
        <ng-container matColumnDef="period"><th mat-header-cell *matHeaderCellDef>{{ 'fs.period' | t }}</th>
          <td mat-cell *matCellDef="let s" class="ltr">{{ s.dateFrom | d }} – {{ s.dateTo | d }}</td></ng-container>
        <ng-container matColumnDef="in"><th mat-header-cell *matHeaderCellDef class="num">{{ 'bank.in' | t }}</th>
          <td mat-cell *matCellDef="let s" class="num ltr">{{ s.inflows | money }}</td></ng-container>
        <ng-container matColumnDef="out"><th mat-header-cell *matHeaderCellDef class="num">{{ 'bank.out' | t }}</th>
          <td mat-cell *matCellDef="let s" class="num ltr">{{ s.outflows | money }}</td></ng-container>
        <ng-container matColumnDef="closing"><th mat-header-cell *matHeaderCellDef class="num">{{ 'bank.closingBalance' | t }}</th>
          <td mat-cell *matCellDef="let s" class="num ltr">{{ s.closingBalance === null ? '—' : (s.closingBalance | money) }}</td></ng-container>
        <ng-container matColumnDef="status"><th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
          <td mat-cell *matCellDef="let s"><span class="chip" [class.ok]="s.status === 'Reconciled'" [class.warn]="s.status === 'Open'">{{ 'bank.status.' + s.status | t }}</span>
            @if (s.unmatchedCount) { <span class="secondary">{{ s.unmatchedCount }} {{ 'bank.toMatch' | t }}</span> }</td></ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns" class="clickable" (click)="router.navigate(['/finance/bank', row.id])"></tr>
      </table>
      @if (statements().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `,
  styles: `
    .two-col { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; }
    @media (max-width: 1000px) { .two-col { grid-template-columns: 1fr; } }
    .compact { max-height: 340px; overflow: auto; }
    .out { color: #8c1d18; }
    .stat.bad .value { color: #b3261e; }
    .stat.good .value { color: #0b5a35; }
    .import { margin-top: 16px; }
    .import .actions { align-items: center; }
    .closing { width: 220px; }
    .spacer { flex: 1; }
    .result { margin-top: 8px; font-weight: 600; }
  `
})
export class BankPage implements OnInit {
  protected readonly router = inject(Router);
  private readonly finance = inject(FinanceApi);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly columns = ['no', 'period', 'in', 'out', 'closing', 'status'];
  protected readonly accounts = signal<Account[]>([]);
  protected readonly statements = signal<BankStatement[]>([]);
  protected readonly rec = signal<BankReconciliation | null>(null);
  protected readonly result = signal<ImportResult | null>(null);
  protected readonly file = signal<File | null>(null);
  protected readonly busy = signal(false);
  protected readonly accountId = new FormControl('', { nonNullable: true });
  protected readonly asOf = new FormControl(todayIso(), { nonNullable: true });
  protected readonly closing = new FormControl<number | null>(null);

  ngOnInit(): void {
    this.api.accounts().subscribe((list) => {
      const banks = list.filter((a) => a.isPostable && a.subtype === 'bank');
      this.accounts.set(banks);
      if (banks.length) {
        this.accountId.setValue(banks[0].id);
        this.load();
      }
    });
  }

  protected load(): void {
    this.finance.statements(this.accountId.value).subscribe((s) => this.statements.set(s));
    this.loadReconciliation();
  }

  protected loadReconciliation(): void {
    if (!this.accountId.value) return;
    this.finance.reconciliation(this.accountId.value, this.asOf.value).subscribe({ next: (r) => this.rec.set(r), error: (e) => this.notify.error(e) });
  }

  protected template(): void {
    this.finance.statementTemplate().subscribe({ error: (e) => this.notify.error(e) });
  }

  protected import(): void {
    const file = this.file();
    if (!file) return;
    this.busy.set(true);
    this.finance.importStatement(this.accountId.value, file, this.closing.value === null ? null : Number(this.closing.value)).subscribe({
      next: (r) => {
        this.busy.set(false);
        this.result.set(r);
        this.file.set(null);
        this.closing.setValue(null);
        this.load();
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }
}

/** Pick the ledger lines that make up one statement line; their total must equal it. */
@Component({
  selector: 'app-match-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'bank.match' | t }}</h2>
    <mat-dialog-content>
      <p><span class="ltr">{{ data.line.date | d }}</span> · {{ data.line.description }} · <b class="ltr">{{ data.line.amount | money }}</b></p>
      <table class="pick">
        <thead><tr><th></th><th>{{ 'app.date' | t }}</th><th>{{ 'cust.document' | t }}</th><th class="num">{{ 'app.amount' | t }}</th></tr></thead>
        <tbody>
          @for (c of candidates(); track c.journalLineId) {
            <tr (click)="toggle(c)" [class.picked]="picked().has(c.journalLineId)">
              <td><mat-checkbox [checked]="picked().has(c.journalLineId)" (click)="$event.preventDefault()" /></td>
              <td class="ltr">{{ c.postingDate | d }}</td>
              <td><span class="ltr">{{ c.sourceNo }}</span><div class="secondary">{{ c.memo }}</div></td>
              <td class="num ltr" [class.exact]="c.amount === data.line.amount">{{ c.amount | money }}</td>
            </tr>
          }
        </tbody>
      </table>
      @if (candidates().length === 0) { <div class="empty">{{ 'bank.noCandidates' | t }}</div> }
      <div class="sum" [class.ok]="selectedTotal() === data.line.amount">{{ 'bank.selected' | t }}: <b class="ltr">{{ selectedTotal() | money }}</b></div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="selectedTotal() !== data.line.amount || busy()" (click)="save()">{{ 'bank.match' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `
    .pick { width: 100%; border-collapse: collapse; }
    .pick th, .pick td { padding: 6px 8px; border-bottom: 1px solid var(--mat-sys-outline-variant); text-align: start; }
    .pick .num { text-align: end; }
    .pick tr { cursor: pointer; }
    .pick tr.picked { background: var(--mat-sys-secondary-container); }
    .exact { font-weight: 700; color: #0b5a35; }
    .sum { margin-top: 12px; text-align: end; }
    .sum.ok b { color: #0b5a35; }
  `
})
export class MatchDialog {
  protected readonly data = inject<{ line: BankLine }>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<MatchDialog, boolean>);
  private readonly finance = inject(FinanceApi);
  private readonly notify = inject(NotifyService);
  protected readonly candidates = signal<LedgerItem[]>([]);
  protected readonly picked = signal(new Set<string>());
  protected readonly busy = signal(false);
  protected readonly selectedTotal = computed(() =>
    Math.round(this.candidates().filter((c) => this.picked().has(c.journalLineId)).reduce((s, c) => s + c.amount, 0) * 100) / 100);

  constructor() {
    this.finance.candidates(this.data.line.id).subscribe((c) => {
      this.candidates.set(c);
      const exact = c.filter((x) => x.amount === this.data.line.amount);
      if (exact.length === 1) this.picked.set(new Set([exact[0].journalLineId]));
    });
  }

  protected toggle(c: LedgerItem): void {
    const next = new Set(this.picked());
    if (next.has(c.journalLineId)) next.delete(c.journalLineId);
    else next.add(c.journalLineId);
    this.picked.set(next);
  }

  protected save(): void {
    this.busy.set(true);
    this.finance.match(this.data.line.id, [...this.picked()]).subscribe({
      next: () => this.ref.close(true),
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }
}

/** Record a bank-only item (charges, interest, an unrecorded transfer) from a statement line. */
@Component({
  selector: 'app-post-line-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'bank.postLine' | t }}</h2>
    <mat-dialog-content>
      <p><span class="ltr">{{ data.line.date | d }}</span> · {{ data.line.description }} · <b class="ltr">{{ data.line.amount | money }}</b></p>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field class="full">
          <mat-label>{{ (data.line.amount < 0 ? 'bank.expenseAccount' : 'bank.incomeAccount') | t }}</mat-label>
          <mat-select formControlName="accountId">
            @for (a of accounts(); track a.id) { <mat-option [value]="a.id"><span class="ltr">{{ a.code }}</span> · {{ a | bi }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'jv.costCenter' | t }}</mat-label>
          <mat-select formControlName="costCenterId">
            <mat-option [value]="null">—</mat-option>
            @for (c of costCenters(); track c.id) { <mat-option [value]="c.id">{{ c.code }} · {{ c | bi }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field class="full">
          <mat-label>{{ 'po.description' | t }}</mat-label>
          <input matInput formControlName="description" />
        </mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid || busy()" (click)="save()">{{ 'doc.post' | t }}</button>
    </mat-dialog-actions>
  `
})
export class PostLineDialog {
  protected readonly data = inject<{ line: BankLine; bankAccountId: string }>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<PostLineDialog, boolean>);
  private readonly finance = inject(FinanceApi);
  private readonly notify = inject(NotifyService);
  protected readonly accounts = signal<Account[]>([]);
  protected readonly costCenters = signal<CostCenter[]>([]);
  protected readonly busy = signal(false);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    accountId: ['', Validators.required],
    costCenterId: [null as string | null],
    description: [this.data.line.description ?? '']
  });

  constructor() {
    inject(ApiService)
      .accounts()
      .subscribe((list) => {
        const usable = list.filter((a) => a.isPostable && !a.isControl && a.id !== this.data.bankAccountId);
        this.accounts.set(usable);
        // Bank charges are the usual case for money out.
        if (this.data.line.amount < 0) this.form.patchValue({ accountId: usable.find((a) => a.code === '5340')?.id ?? '' });
      });
    this.finance.costCenters().subscribe((c) => this.costCenters.set(c.filter((x) => x.isActive)));
  }

  protected save(): void {
    this.busy.set(true);
    this.finance.postLine(this.data.line.id, this.form.getRawValue()).subscribe({
      next: () => this.ref.close(true),
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }
}

@Component({
  selector: 'app-bank-statement',
  imports: [...SHARED],
  template: `
    @if (statement(); as s) {
      <div class="page-header">
        <a mat-icon-button routerLink="/finance/bank"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
        <h1>{{ 'bank.statementNo' | t }} <span class="ltr">{{ s.statementNo }}</span></h1>
        <span class="chip" [class.ok]="s.status === 'Reconciled'" [class.warn]="s.status === 'Open'">{{ 'bank.status.' + s.status | t }}</span>
      </div>
      <div class="actions header-actions">
        <button mat-flat-button (click)="autoMatch()" [disabled]="busy() || s.unmatchedCount === 0"><mat-icon>auto_fix_high</mat-icon> {{ 'bank.autoMatch' | t }}</button>
        <button mat-button color="warn" (click)="remove()" [disabled]="busy()"><mat-icon>delete</mat-icon> {{ 'app.delete' | t }}</button>
      </div>
      <div class="facts">
        <span>{{ 'bank.account' | t }}: <b><span class="ltr">{{ s.accountCode }}</span> · {{ s | bi:'accountName' }}</b></span>
        <span>{{ 'fs.period' | t }}: <b class="ltr">{{ s.dateFrom | d }} – {{ s.dateTo | d }}</b></span>
        <span>{{ 'bank.in' | t }}: <b class="ltr">{{ s.inflows | money }}</b></span>
        <span>{{ 'bank.out' | t }}: <b class="ltr">{{ s.outflows | money }}</b></span>
        @if (s.closingBalance !== null) { <span>{{ 'bank.closingBalance' | t }}: <b class="ltr">{{ s.closingBalance | money }}</b></span> }
      </div>
      @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
      <div class="table-wrap">
        <table mat-table [dataSource]="s.lines ?? []">
          <ng-container matColumnDef="date"><th mat-header-cell *matHeaderCellDef>{{ 'app.date' | t }}</th>
            <td mat-cell *matCellDef="let l" class="ltr">{{ l.date | d }}</td></ng-container>
          <ng-container matColumnDef="desc"><th mat-header-cell *matHeaderCellDef>{{ 'cust.details' | t }}</th>
            <td mat-cell *matCellDef="let l">{{ l.description }}<div class="secondary ltr">{{ l.reference }}</div></td></ng-container>
          <ng-container matColumnDef="amount"><th mat-header-cell *matHeaderCellDef class="num">{{ 'app.amount' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr" [class.out]="l.amount < 0">{{ l.amount | money }}</td></ng-container>
          <ng-container matColumnDef="status"><th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
            <td mat-cell *matCellDef="let l"><span [class]="chip(l.status)">{{ 'bank.line.' + l.status | t }}</span></td></ng-container>
          <ng-container matColumnDef="matched"><th mat-header-cell *matHeaderCellDef>{{ 'bank.matchedWith' | t }}</th>
            <td mat-cell *matCellDef="let l">@for (m of l.matches; track m.journalLineId) { <div><span class="ltr">{{ m.sourceNo }}</span> <span class="secondary">{{ m.memo }}</span></div> }</td></ng-container>
          <ng-container matColumnDef="actions"><th mat-header-cell *matHeaderCellDef></th>
            <td mat-cell *matCellDef="let l">
              @if (l.status === 'Unmatched') {
                <button mat-icon-button (click)="match(l)" [matTooltip]="'bank.match' | t"><mat-icon>link</mat-icon></button>
                <button mat-icon-button (click)="post(l)" [matTooltip]="'bank.postLine' | t"><mat-icon>post_add</mat-icon></button>
                <button mat-icon-button (click)="ignore(l, true)" [matTooltip]="'bank.ignore' | t"><mat-icon>visibility_off</mat-icon></button>
              }
              @if (l.status === 'Matched' && !l.journalEntryId) {
                <button mat-icon-button (click)="unmatch(l)" [matTooltip]="'bank.unmatch' | t"><mat-icon>link_off</mat-icon></button>
              }
              @if (l.status === 'Ignored') {
                <button mat-icon-button (click)="ignore(l, false)" [matTooltip]="'bank.restore' | t"><mat-icon>visibility</mat-icon></button>
              }
            </td></ng-container>
          <tr mat-header-row *matHeaderRowDef="columns"></tr>
          <tr mat-row *matRowDef="let row; columns: columns"></tr>
        </table>
      </div>
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: `
    .header-actions { margin-block: -8px 16px; }
    .out { color: #8c1d18; }
  `
})
export class BankStatementPage implements OnInit {
  readonly id = input.required<string>();
  private readonly finance = inject(FinanceApi);
  private readonly notify = inject(NotifyService);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  protected readonly chip = lineChip;
  protected readonly columns = ['date', 'desc', 'amount', 'status', 'matched', 'actions'];
  protected readonly statement = signal<BankStatement | null>(null);
  protected readonly busy = signal(false);

  ngOnInit(): void {
    this.load();
  }

  protected autoMatch(): void {
    this.busy.set(true);
    this.finance.autoMatch(this.id()).subscribe({
      next: (r) => {
        this.busy.set(false);
        this.notify.success(r.matched ? 'bank.autoMatched' : 'bank.nothingMatched');
        this.load();
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }

  protected match(line: BankLine): void {
    this.dialog.open(MatchDialog, { data: { line }, width: '760px' }).afterClosed().subscribe((ok) => ok && this.load());
  }

  protected post(line: BankLine): void {
    this.dialog
      .open(PostLineDialog, { data: { line, bankAccountId: this.statement()!.accountId }, width: '560px' })
      .afterClosed()
      .subscribe((ok) => ok && this.load());
  }

  protected unmatch(line: BankLine): void {
    this.finance.unmatch(line.id).subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  protected ignore(line: BankLine, ignored: boolean): void {
    this.finance.ignore(line.id, ignored).subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  protected remove(): void {
    this.dialog
      .open(ConfirmDialog, { data: { title: 'app.delete', message: 'bank.deleteConfirm' } })
      .afterClosed()
      .pipe(filter(Boolean), switchMap(() => this.finance.deleteStatement(this.id())))
      .subscribe({ next: () => void this.router.navigate(['/finance/bank']), error: (e) => this.notify.error(e) });
  }

  private load(): void {
    this.finance.statement(this.id()).subscribe({ next: (s) => this.statement.set(s), error: (e) => this.notify.error(e) });
  }
}
