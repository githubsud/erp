import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormControl } from '@angular/forms';
import { toIso, todayIso } from '../../core/dates';
import { BalanceSheet, CashFlow, FinanceApi, ProfitAndLoss, StatementSection } from '../../core/finance.api';
import { CostCenter } from '../../core/finance.api';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

type Preset = 'thisMonth' | 'lastMonth' | 'thisQuarter' | 'ytd' | 'lastYear';
type Compare = 'none' | 'previous' | 'lastYear';

/** The same date in another year (29 February becomes the 28th). */
function shiftYear(iso: string, years: number): string {
  const shifted = `${Number(iso.slice(0, 4)) + years}${iso.slice(4)}`;
  return shifted.endsWith('-02-29') && new Date(Number(shifted.slice(0, 4)), 1, 29).getMonth() !== 1 ? `${shifted.slice(0, 8)}28` : shifted;
}

/** Income statement and balance sheet with optional comparison, printable as PDF. */
@Component({
  selector: 'app-statements',
  imports: [...SHARED, NgTemplateOutlet],
  template: `
    <div class="page-header"><h1>{{ 'fs.title' | t }}</h1></div>
    <mat-tab-group mat-stretch-tabs="false" (selectedIndexChange)="tab.set($event)">
      <mat-tab [label]="'fs.pl' | t">
        <div class="toolbar-row tab-body">
          <mat-form-field>
            <mat-label>{{ 'fs.period' | t }}</mat-label>
            <mat-select [formControl]="preset" (selectionChange)="applyPreset()">
              @for (p of presets; track p) { <mat-option [value]="p">{{ 'fs.preset.' + p | t }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <mat-form-field><mat-label>{{ 'app.from' | t }}</mat-label><input matInput type="date" [formControl]="from" /></mat-form-field>
          <mat-form-field><mat-label>{{ 'app.to' | t }}</mat-label><input matInput type="date" [formControl]="to" /></mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'fs.compare' | t }}</mat-label>
            <mat-select [formControl]="compare">
              <mat-option value="none">{{ 'fs.compare.none' | t }}</mat-option>
              <mat-option value="previous">{{ 'fs.compare.previous' | t }}</mat-option>
              <mat-option value="lastYear">{{ 'fs.compare.lastYear' | t }}</mat-option>
            </mat-select>
          </mat-form-field>
          @if (costCenters().length) {
            <mat-form-field>
              <mat-label>{{ 'fs.costCenter' | t }}</mat-label>
              <mat-select [formControl]="costCenter">
                <mat-option value="">{{ 'fs.allCostCenters' | t }}</mat-option>
                @for (cc of costCenters(); track cc.id) { <mat-option [value]="cc.id"><span class="ltr">{{ cc.code }}</span> · {{ cc | bi }}</mat-option> }
              </mat-select>
            </mat-form-field>
          }
          <button mat-flat-button (click)="loadPl()">{{ 'finance.run' | t }}</button>
          <button mat-stroked-button (click)="plPdf()"><mat-icon>picture_as_pdf</mat-icon> PDF</button>
        </div>
        @if (pl(); as r) {
          <mat-card appearance="outlined" class="statement">
            <div class="row head">
              <span></span><span class="num period"><span class="ltr">{{ r.from | d }} – {{ r.to | d }}</span></span>
              @if (r.compareFrom) { <span class="num period secondary"><span class="ltr">{{ r.compareFrom | d }} – {{ r.compareTo | d }}</span></span> }
            </div>
            @for (s of plTop(); track s.code) { <ng-container [ngTemplateOutlet]="section" [ngTemplateOutletContext]="{ $implicit: s, compare: !!r.compareFrom }" /> }
            <div class="row total"><span>{{ 'fs.grossProfit' | t }}</span><span class="num ltr">{{ r.grossProfit | money }}</span>
              @if (r.compareFrom) { <span class="num ltr secondary">{{ r.compareGrossProfit | money }}</span> }</div>
            @for (s of plExpenses(); track s.code) { <ng-container [ngTemplateOutlet]="section" [ngTemplateOutletContext]="{ $implicit: s, compare: !!r.compareFrom }" /> }
            <div class="row total strong" [class.loss]="r.netProfit < 0"><span>{{ 'fs.netProfit' | t }}</span><span class="num ltr">{{ r.netProfit | money }}</span>
              @if (r.compareFrom) { <span class="num ltr secondary">{{ r.compareNetProfit | money }}</span> }</div>
            @if (r.revenue) {
              <div class="ratios secondary">
                {{ 'fs.grossMargin' | t }} <b class="ltr">{{ pct(r.grossProfit, r.revenue) }}</b> · {{ 'fs.netMargin' | t }} <b class="ltr">{{ pct(r.netProfit, r.revenue) }}</b>
              </div>
            }
          </mat-card>
        }
      </mat-tab>

      <mat-tab [label]="'fs.cf' | t">
        <div class="toolbar-row tab-body">
          <mat-form-field><mat-label>{{ 'app.from' | t }}</mat-label><input matInput type="date" [formControl]="from" /></mat-form-field>
          <mat-form-field><mat-label>{{ 'app.to' | t }}</mat-label><input matInput type="date" [formControl]="to" /></mat-form-field>
          <button mat-flat-button (click)="loadCf()">{{ 'finance.run' | t }}</button>
        </div>
        @if (cf(); as r) {
          <mat-card appearance="outlined" class="statement">
            @for (s of r.sections; track s.kind) {
              <div class="row section-title"><span>{{ 'fs.cf.' + s.kind | t }}</span></div>
              @for (l of s.lines; track l.code + l.nameEn) {
                <div class="row"><span>@if (l.code) { <span class="ltr secondary">{{ l.code }}</span> } {{ l.code ? (l | bi) : ('fs.cf.netProfit' | t) }}</span>
                  <span class="num ltr">{{ l.amount | money }}</span></div>
              }
              <div class="row total"><span>{{ 'fs.cf.total.' + s.kind | t }}</span><span class="num ltr">{{ s.total | money }}</span></div>
            }
            <div class="row total strong"><span>{{ 'fs.cf.netChange' | t }}</span><span class="num ltr">{{ r.netChange | money }}</span></div>
            <div class="row"><span>{{ 'fs.cf.opening' | t }}</span><span class="num ltr">{{ r.openingCash | money }}</span></div>
            <div class="row total"><span>{{ 'fs.cf.closing' | t }}</span><span class="num ltr">{{ r.closingCash | money }}</span></div>
            @if (!r.reconciles) { <div class="error-panel">{{ 'fs.cf.mismatch' | t }}</div> }
          </mat-card>
        }
      </mat-tab>

      <mat-tab [label]="'fs.bs' | t">
        <div class="toolbar-row tab-body">
          <mat-form-field><mat-label>{{ 'stock.asOf' | t }}</mat-label><input matInput type="date" [formControl]="asOf" /></mat-form-field>
          <mat-slide-toggle [formControl]="bsCompare">{{ 'fs.compareLastYear' | t }}</mat-slide-toggle>
          <button mat-flat-button (click)="loadBs()">{{ 'finance.run' | t }}</button>
          <button mat-stroked-button (click)="bsPdf()"><mat-icon>picture_as_pdf</mat-icon> PDF</button>
        </div>
        @if (bs(); as r) {
          <mat-card appearance="outlined" class="statement">
            <div class="row head">
              <span><span class="chip" [class.ok]="r.isBalanced" [class.bad]="!r.isBalanced">{{ (r.isBalanced ? 'finance.balanced' : 'finance.notBalanced') | t }}</span></span>
              <span class="num">{{ r.asOf | d }}</span>
              @if (r.compareAsOf) { <span class="num secondary">{{ r.compareAsOf | d }}</span> }
            </div>
            @for (s of bsKind('asset'); track s.code) { <ng-container [ngTemplateOutlet]="section" [ngTemplateOutletContext]="{ $implicit: s, compare: !!r.compareAsOf }" /> }
            <div class="row total strong"><span>{{ 'fs.totalAssets' | t }}</span><span class="num ltr">{{ r.totalAssets | money }}</span>
              @if (r.compareAsOf) { <span class="num ltr secondary">{{ r.compareTotalAssets | money }}</span> }</div>
            @for (s of bsKind('liability'); track s.code) { <ng-container [ngTemplateOutlet]="section" [ngTemplateOutletContext]="{ $implicit: s, compare: !!r.compareAsOf }" /> }
            <div class="row total"><span>{{ 'fs.totalLiabilities' | t }}</span><span class="num ltr">{{ r.totalLiabilities | money }}</span>
              @if (r.compareAsOf) { <span class="num ltr secondary">{{ r.compareTotalLiabilities | money }}</span> }</div>
            @for (s of bsKind('equity'); track s.code) { <ng-container [ngTemplateOutlet]="section" [ngTemplateOutletContext]="{ $implicit: s, compare: !!r.compareAsOf }" /> }
            <div class="row total strong"><span>{{ 'fs.totalLiabilitiesEquity' | t }}</span><span class="num ltr">{{ r.totalLiabilities + r.totalEquity | money }}</span>
              @if (r.compareAsOf) { <span class="num ltr secondary">{{ (r.compareTotalLiabilities ?? 0) + (r.compareTotalEquity ?? 0) | money }}</span> }</div>
          </mat-card>
        }
      </mat-tab>
    </mat-tab-group>

    <ng-template #section let-s let-compare="compare">
      <div class="row section-head"><span>{{ s | bi }}</span><span></span>@if (compare) { <span></span> }</div>
      @for (a of s.accounts; track a.code + a.nameEn) {
        <div class="row account">
          <span>@if (a.code) { <span class="ltr secondary code">{{ a.code }}</span> } {{ a | bi }}</span>
          <span class="num ltr">{{ a.amount | money }}</span>
          @if (compare) { <span class="num ltr secondary">{{ a.compare | money }}</span> }
        </div>
      }
      <div class="row subtotal"><span>{{ 'app.total' | t }} {{ s | bi }}</span><span class="num ltr">{{ s.total | money }}</span>
        @if (compare) { <span class="num ltr secondary">{{ s.compareTotal | money }}</span> }</div>
    </ng-template>
  `,
  styles: `
    .tab-body { margin-block-start: 16px; }
    .statement { padding: 8px 20px 16px; max-width: 900px; }
    .row { display: grid; grid-template-columns: 1fr 190px 190px; gap: 12px; padding: 6px 0; align-items: center; }
    .period { font-size: 12px; }
    .row.head { font-weight: 600; border-bottom: 1px solid var(--mat-sys-outline-variant); }
    .section-head { margin-top: 12px; font-weight: 700; color: var(--mat-sys-primary); border-bottom: 1px solid var(--mat-sys-outline-variant); }
    .account { padding-inline-start: 16px; border-bottom: 1px dashed var(--mat-sys-outline-variant); }
    .code { margin-inline-end: 6px; }
    .subtotal { font-weight: 600; }
    .total { font-weight: 700; border-top: 2px solid var(--mat-sys-outline); margin-top: 8px; }
    .total.strong { font-size: 1.05em; color: var(--mat-sys-primary); }
    .total.loss { color: #b3261e; }
    .ratios { margin-top: 12px; }
    @media (max-width: 700px) { .row { grid-template-columns: 1fr 110px 110px; } }
  `
})
export class StatementsPage {
  private readonly finance = inject(FinanceApi);
  private readonly notify = inject(NotifyService);
  protected readonly presets: Preset[] = ['thisMonth', 'lastMonth', 'thisQuarter', 'ytd', 'lastYear'];
  protected readonly tab = signal(0);
  protected readonly preset = new FormControl<Preset>('ytd', { nonNullable: true });
  protected readonly from = new FormControl('', { nonNullable: true });
  protected readonly to = new FormControl('', { nonNullable: true });
  protected readonly compare = new FormControl<Compare>('lastYear', { nonNullable: true });
  protected readonly asOf = new FormControl(todayIso(), { nonNullable: true });
  protected readonly bsCompare = new FormControl(false, { nonNullable: true });
  protected readonly pl = signal<ProfitAndLoss | null>(null);
  protected readonly cf = signal<CashFlow | null>(null);
  protected readonly costCenters = signal<CostCenter[]>([]);
  protected readonly costCenter = new FormControl('', { nonNullable: true });
  protected readonly bs = signal<BalanceSheet | null>(null);
  protected readonly plTop = computed(() => this.pl()?.sections.filter((s) => s.kind === 'income' || s.kind === 'cost_of_sales') ?? []);
  protected readonly plExpenses = computed(() => this.pl()?.sections.filter((s) => s.kind === 'expense') ?? []);

  constructor() {
    this.applyPreset();
    this.loadPl();
    this.loadBs();
    this.loadCf();
    this.finance.costCenters().subscribe((c) => this.costCenters.set(c.filter((x) => x.isActive)));
  }

  protected loadCf(): void {
    this.finance.cashFlow(this.from.value, this.to.value).subscribe({ next: (r) => this.cf.set(r), error: (e) => this.notify.error(e) });
  }

  protected bsKind(kind: StatementSection['kind']): StatementSection[] {
    return this.bs()?.sections.filter((s) => s.kind === kind) ?? [];
  }

  protected pct(part: number, whole: number): string {
    return `${((part * 100) / whole).toFixed(1)}%`;
  }

  protected applyPreset(): void {
    const now = new Date();
    const y = now.getFullYear();
    const m = now.getMonth();
    const range: Record<Preset, [Date, Date]> = {
      thisMonth: [new Date(y, m, 1), now],
      lastMonth: [new Date(y, m - 1, 1), new Date(y, m, 0)],
      thisQuarter: [new Date(y, Math.floor(m / 3) * 3, 1), now],
      ytd: [new Date(y, 0, 1), now],
      lastYear: [new Date(y - 1, 0, 1), new Date(y - 1, 11, 31)]
    };
    const [f, t] = range[this.preset.value];
    this.from.setValue(toIso(f));
    this.to.setValue(toIso(t));
  }

  protected loadPl(): void {
    const [cf, ct] = this.compareRange();
    this.finance.profitAndLoss(this.from.value, this.to.value, cf, ct, this.costCenter.value || null).subscribe({ next: (r) => this.pl.set(r), error: (e) => this.notify.error(e) });
  }

  protected plPdf(): void {
    const [cf, ct] = this.compareRange();
    this.finance.profitAndLossPdf(this.from.value, this.to.value, cf, ct).subscribe({ error: (e) => this.notify.error(e) });
  }

  protected loadBs(): void {
    this.finance.balanceSheet(this.asOf.value, this.bsCompareDate()).subscribe({ next: (r) => this.bs.set(r), error: (e) => this.notify.error(e) });
  }

  protected bsPdf(): void {
    this.finance.balanceSheetPdf(this.asOf.value, this.bsCompareDate()).subscribe({ error: (e) => this.notify.error(e) });
  }

  private bsCompareDate(): string | null {
    return this.bsCompare.value ? shiftYear(this.asOf.value, -1) : null;
  }

  /** The comparison period: the same dates a year earlier, or the period of equal length just before. */
  private compareRange(): [string | null, string | null] {
    const f = this.from.value;
    const t = this.to.value;
    if (this.compare.value === 'lastYear') return [shiftYear(f, -1), shiftYear(t, -1)];
    if (this.compare.value === 'previous') {
      const start = new Date(+f.slice(0, 4), +f.slice(5, 7) - 1, +f.slice(8, 10));
      const end = new Date(+t.slice(0, 4), +t.slice(5, 7) - 1, +t.slice(8, 10));
      const days = Math.round((end.getTime() - start.getTime()) / 86_400_000) + 1;
      const prevEnd = new Date(start.getFullYear(), start.getMonth(), start.getDate() - 1);
      const prevStart = new Date(prevEnd.getFullYear(), prevEnd.getMonth(), prevEnd.getDate() - days + 1);
      return [toIso(prevStart), toIso(prevEnd)];
    }
    return [null, null];
  }
}
