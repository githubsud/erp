import { Component, inject, OnInit, signal } from '@angular/core';
import { FormControl } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { todayIso } from '../../core/dates';
import { AuthService } from '../../core/auth.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { Account, PayrollRun } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { NATIONALITIES, SHARED } from '../../shared/shared';

interface RegisterColumn {
  code: string;
  nameEn: string;
  nameAr: string;
  kind: string;
}

interface Register {
  runNo: string;
  columns: RegisterColumn[];
  rows: { employeeId: string; empNo: string; nameEn: string; nameAr: string; departmentEn: string | null; departmentAr: string | null; inWps: boolean;
    paidDays: number; amounts: Record<string, number>; gross: number; deductions: number; net: number }[];
  totals: Record<string, number>;
  gross: number;
  deductions: number;
  net: number;
}

interface CountRow {
  key: string;
  labelEn: string;
  labelAr: string;
  count: number;
  male: number;
  female: number;
}

interface Headcount {
  total: number;
  qatari: number;
  qatariPercent: number;
  joinedThisYear: number;
  leftThisYear: number;
  byNationality: CountRow[];
  byDepartment: CountRow[];
}

interface LeaveLiability {
  rows: { employeeId: string; empNo: string; nameEn: string; nameAr: string; balanceDays: number; dailyRate: number; liability: number; provision: number }[];
  totalLiability: number;
  totalProvision: number;
  ledgerBalance: number;
}

interface WpsRow {
  runId: string;
  runNo: string;
  month: number;
  status: string;
  wpsDeadline: string;
  payrollNet: number;
  wpsNet: number;
  wpsEmployees: number;
  outsideWpsNet: number;
  outsideWpsEmployees: number;
  sifTotal: number | null;
  sifRecords: number | null;
  sifStatus: string | null;
  paymentDate: string | null;
  matches: boolean;
  issues: string[];
}

interface GrsiaMonth {
  month: number;
  employees: number;
  employeeShare: number;
  employerShare: number;
  total: number;
  paidOn: string | null;
  paidAmount: number | null;
}

interface GrsiaSchedule {
  year: number;
  month: number;
  rows: {
    employeeId: string;
    empNo: string;
    nameEn: string;
    nameAr: string;
    qid: string | null;
    contributionSalary: number;
    employeeShare: number;
    employerShare: number;
    total: number;
  }[];
  contributionSalary: number;
  employeeShare: number;
  employerShare: number;
  total: number;
  allPosted: boolean;
  payment: { amount: number; paymentDate: string; reference: string | null } | null;
  ledgerBalance: number;
}

@Component({
  selector: 'app-hr-reports',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'hrr.title' | t }}</h1></div>
    <mat-tab-group mat-stretch-tabs="false" (selectedIndexChange)="select($event)">
      <mat-tab [label]="'hrr.register' | t">
        <div class="toolbar-row tab-body">
          <mat-form-field>
            <mat-label>{{ 'nav.payroll' | t }}</mat-label>
            <mat-select [formControl]="runId" (selectionChange)="loadRegister()">
              @for (r of runs(); track r.id) { <mat-option [value]="r.id"><span class="ltr">{{ r.runNo }}</span> · {{ 'month.' + r.month | t }} {{ r.year }}</mat-option> }
            </mat-select>
          </mat-form-field>
          @if (register()) {
            <button mat-stroked-button (click)="excel()"><mat-icon>table_view</mat-icon> Excel</button>
          }
        </div>
        @if (register(); as r) {
          <div class="table-wrap">
            <table class="line-editor compact">
              <thead><tr><th>{{ 'employee.empNo' | t }}</th><th>{{ 'item.name' | t }}</th><th class="num">{{ 'payroll.paidDays' | t }}</th>
                @for (c of r.columns; track c.code) { <th class="num" [class.ded]="c.kind === 'Deduction'">{{ c | bi }}</th> }
                <th class="num">{{ 'me.gross' | t }}</th><th class="num">{{ 'me.deductions' | t }}</th><th class="num">{{ 'me.netPay' | t }}</th></tr></thead>
              <tbody>
                @for (x of r.rows; track x.employeeId) {
                  <tr>
                    <td class="ltr">{{ x.empNo }}</td>
                    <td>{{ x | bi }} @if (!x.inWps) { <span class="chip warn">{{ 'hrr.outsideWps' | t }}</span> }<div class="secondary">{{ x | bi:'department' }}</div></td>
                    <td class="num ltr">{{ x.paidDays }}</td>
                    @for (c of r.columns; track c.code) { <td class="num ltr">{{ x.amounts[c.code] ? (x.amounts[c.code] | money) : '' }}</td> }
                    <td class="num ltr">{{ x.gross | money }}</td><td class="num ltr">{{ x.deductions | money }}</td><td class="num ltr"><b>{{ x.net | money }}</b></td>
                  </tr>
                }
                <tr class="total-row"><td colspan="3">{{ 'app.total' | t }}</td>
                  @for (c of r.columns; track c.code) { <td class="num ltr">{{ r.totals[c.code] | money }}</td> }
                  <td class="num ltr">{{ r.gross | money }}</td><td class="num ltr">{{ r.deductions | money }}</td><td class="num ltr">{{ r.net | money }}</td></tr>
              </tbody>
            </table>
          </div>
        }
      </mat-tab>

      <mat-tab [label]="'hrr.headcount' | t">
        <div class="toolbar-row tab-body">
          <mat-form-field><mat-label>{{ 'stock.asOf' | t }}</mat-label><input matInput type="date" [formControl]="asOf" /></mat-form-field>
          <button mat-flat-button (click)="loadHeadcount()">{{ 'finance.run' | t }}</button>
        </div>
        @if (headcount(); as h) {
          <div class="stats">
            <mat-card appearance="outlined" class="stat"><div class="label">{{ 'hrr.total' | t }}</div><div class="value ltr">{{ h.total }}</div></mat-card>
            <mat-card appearance="outlined" class="stat"><div class="label">{{ 'hrr.qatari' | t }}</div><div class="value ltr">{{ h.qatari }} · {{ h.qatariPercent }}%</div></mat-card>
            <mat-card appearance="outlined" class="stat"><div class="label">{{ 'hrr.joined' | t }}</div><div class="value ltr">{{ h.joinedThisYear }}</div></mat-card>
            <mat-card appearance="outlined" class="stat"><div class="label">{{ 'hrr.left' | t }}</div><div class="value ltr">{{ h.leftThisYear }}</div></mat-card>
          </div>
          <div class="two">
            <table class="line-editor">
              <thead><tr><th>{{ 'employee.nationality' | t }}</th><th class="num">{{ 'hrr.count' | t }}</th><th class="num">{{ 'employee.male' | t }}</th><th class="num">{{ 'employee.female' | t }}</th></tr></thead>
              <tbody>@for (n of h.byNationality; track n.key) {
                <tr><td>{{ nationality(n.key) }}</td><td class="num ltr">{{ n.count }}</td><td class="num ltr">{{ n.male }}</td><td class="num ltr">{{ n.female }}</td></tr> }</tbody>
            </table>
            <table class="line-editor">
              <thead><tr><th>{{ 'employee.department' | t }}</th><th class="num">{{ 'hrr.count' | t }}</th><th class="num">{{ 'employee.male' | t }}</th><th class="num">{{ 'employee.female' | t }}</th></tr></thead>
              <tbody>@for (d of h.byDepartment; track d.key) {
                <tr><td>{{ d | bi:'label' }}</td><td class="num ltr">{{ d.count }}</td><td class="num ltr">{{ d.male }}</td><td class="num ltr">{{ d.female }}</td></tr> }</tbody>
            </table>
          </div>
        }
      </mat-tab>

      <mat-tab [label]="'hrr.leave' | t">
        <div class="toolbar-row tab-body">
          <mat-form-field><mat-label>{{ 'stock.asOf' | t }}</mat-label><input matInput type="date" [formControl]="asOf" /></mat-form-field>
          <button mat-flat-button (click)="loadLeave()">{{ 'finance.run' | t }}</button>
        </div>
        @if (leave(); as l) {
          <div class="reconcile" [class.bad]="l.totalProvision !== l.ledgerBalance">
            {{ 'hrr.leaveCheck' | t }} <b class="ltr">{{ l.totalProvision | money }}</b> · {{ 'hrr.ledger' | t }} <b class="ltr">{{ l.ledgerBalance | money }}</b>
          </div>
          <table class="line-editor">
            <thead><tr><th>{{ 'employee.empNo' | t }}</th><th>{{ 'item.name' | t }}</th><th class="num">{{ 'hrr.balanceDays' | t }}</th>
              <th class="num">{{ 'hrr.dailyRate' | t }}</th><th class="num">{{ 'hrr.liability' | t }}</th><th class="num">{{ 'hrr.provision' | t }}</th></tr></thead>
            <tbody>
              @for (x of l.rows; track x.employeeId) {
                <tr><td class="ltr">{{ x.empNo }}</td><td>{{ x | bi }}</td><td class="num ltr">{{ x.balanceDays }}</td><td class="num ltr">{{ x.dailyRate | money }}</td>
                  <td class="num ltr">{{ x.liability | money }}</td><td class="num ltr">{{ x.provision | money }}</td></tr>
              }
              <tr class="total-row"><td colspan="4">{{ 'app.total' | t }}</td><td class="num ltr">{{ l.totalLiability | money }}</td><td class="num ltr">{{ l.totalProvision | money }}</td></tr>
            </tbody>
          </table>
        }
      </mat-tab>

      <mat-tab [label]="'hrr.wps' | t">
        <div class="toolbar-row tab-body">
          <mat-form-field><mat-label>{{ 'hrr.year' | t }}</mat-label><input matInput type="number" [formControl]="year" dir="ltr" /></mat-form-field>
          <button mat-flat-button (click)="loadWps()">{{ 'finance.run' | t }}</button>
        </div>
        <table class="line-editor">
          <thead><tr><th>{{ 'payroll.month' | t }}</th><th>{{ 'app.status' | t }}</th><th class="num">{{ 'hrr.wpsNet' | t }}</th><th class="num">{{ 'hrr.sif' | t }}</th>
            <th class="num">{{ 'hrr.outside' | t }}</th><th>{{ 'hrr.deadline' | t }}</th><th>{{ 'hrr.check' | t }}</th></tr></thead>
          <tbody>
            @for (w of wps(); track w.runId) {
              <tr>
                <td>{{ 'month.' + w.month | t }} <span class="secondary ltr">{{ w.runNo }}</span></td>
                <td>{{ 'payroll.status.' + w.status | t }}</td>
                <td class="num ltr">{{ w.wpsNet | money }} <span class="secondary">({{ w.wpsEmployees }})</span></td>
                <td class="num ltr">@if (w.sifTotal !== null) { {{ w.sifTotal | money }} <span class="secondary">({{ w.sifRecords }})</span> } @else { — }</td>
                <td class="num ltr">@if (w.outsideWpsEmployees) { {{ w.outsideWpsNet | money }} <span class="secondary">({{ w.outsideWpsEmployees }})</span> }</td>
                <td class="ltr">{{ w.wpsDeadline | d }}</td>
                <td>@if (w.matches) { <span class="chip ok">{{ 'hrr.ok' | t }}</span> }
                  @for (i of w.issues; track i) { <span class="chip bad">{{ 'hrr.issue.' + i | t }}</span> }</td>
              </tr>
            }
          </tbody>
        </table>
        @if (!wps().length) { <p class="secondary">{{ 'app.none' | t }}</p> }
      </mat-tab>

      <mat-tab [label]="'grsia.tab' | t">
        <div class="toolbar-row tab-body">
          <mat-form-field><mat-label>{{ 'hrr.year' | t }}</mat-label><input matInput type="number" [formControl]="year" dir="ltr" /></mat-form-field>
          <button mat-flat-button (click)="loadGrsiaYear()">{{ 'finance.run' | t }}</button>
        </div>
        <p class="secondary">{{ 'grsia.hint' | t }}</p>
        <table class="line-editor">
          <thead><tr><th>{{ 'payroll.month' | t }}</th><th class="num">{{ 'grsia.employees' | t }}</th><th class="num">{{ 'grsia.employeeShare' | t }}</th>
            <th class="num">{{ 'grsia.employerShare' | t }}</th><th class="num">{{ 'app.total' | t }}</th><th>{{ 'grsia.paid' | t }}</th><th></th></tr></thead>
          <tbody>
            @for (m of grsiaYear(); track m.month) {
              @if (m.total > 0 || m.paidAmount) {
                <tr>
                  <td class="ltr">{{ period(m.month) }}</td>
                  <td class="num ltr">{{ m.employees }}</td>
                  <td class="num ltr">{{ m.employeeShare | money }}</td>
                  <td class="num ltr">{{ m.employerShare | money }}</td>
                  <td class="num ltr"><b>{{ m.total | money }}</b></td>
                  <td>@if (m.paidOn) { <span class="chip ok ltr">{{ m.paidOn | d }}</span> } @else { <span class="chip warn">{{ 'grsia.unpaid' | t }}</span> }</td>
                  <td><button mat-button (click)="loadGrsia(m.month)">{{ 'app.view' | t }}</button></td>
                </tr>
              }
            }
          </tbody>
        </table>
        @if (grsia(); as g) {
          <h3>{{ 'grsia.schedule' | t }} · <span class="ltr">{{ period(g.month) }}</span>
            <button mat-button (click)="grsiaExcel(g.year, g.month)"><mat-icon>download</mat-icon> Excel</button></h3>
          <div class="reconcile" [class.bad]="!g.payment && g.total !== g.ledgerBalance">
            {{ 'grsia.ledger' | t }}: <span class="ltr">{{ g.ledgerBalance | money }}</span>
          </div>
          <table class="line-editor compact">
            <thead><tr><th>{{ 'grsia.empNo' | t }}</th><th>{{ 'grsia.name' | t }}</th><th>QID</th><th class="num">{{ 'grsia.salary' | t }}</th>
              <th class="num">{{ 'grsia.employeeShare' | t }}</th><th class="num">{{ 'grsia.employerShare' | t }}</th><th class="num">{{ 'app.total' | t }}</th></tr></thead>
            <tbody>
              @for (r of g.rows; track r.employeeId) {
                <tr>
                  <td class="ltr">{{ r.empNo }}</td><td>{{ r | bi }}</td><td class="ltr">{{ r.qid }}</td>
                  <td class="num ltr">{{ r.contributionSalary | money }}</td><td class="num ltr">{{ r.employeeShare | money }}</td>
                  <td class="num ltr">{{ r.employerShare | money }}</td><td class="num ltr">{{ r.total | money }}</td>
                </tr>
              }
              <tr class="total"><td colspan="3">{{ 'app.total' | t }}</td><td class="num ltr">{{ g.contributionSalary | money }}</td>
                <td class="num ltr">{{ g.employeeShare | money }}</td><td class="num ltr">{{ g.employerShare | money }}</td><td class="num ltr"><b>{{ g.total | money }}</b></td></tr>
            </tbody>
          </table>
          @if (g.payment; as p) {
            <p>{{ 'grsia.paidOn' | t }} <span class="ltr">{{ p.paymentDate | d }}</span> · <span class="ltr">{{ p.amount | money }}</span>
              @if (p.reference) { · <span class="ltr">{{ p.reference }}</span> }</p>
          } @else if (canPay && g.allPosted && g.total > 0) {
            <div class="toolbar-row">
              <mat-form-field><mat-label>{{ 'grsia.payFrom' | t }}</mat-label>
                <mat-select [formControl]="payAccount">@for (a of bankAccounts(); track a.id) { <mat-option [value]="a.id"><span class="ltr">{{ a.code }}</span> {{ a | bi }}</mat-option> }</mat-select>
              </mat-form-field>
              <mat-form-field><mat-label>{{ 'app.date' | t }}</mat-label><input matInput type="date" [formControl]="payDate" /></mat-form-field>
              <mat-form-field><mat-label>{{ 'grsia.reference' | t }}</mat-label><input matInput class="ltr" [formControl]="payRef" /></mat-form-field>
              <button mat-flat-button [disabled]="!payAccount.value" (click)="payGrsia(g.year, g.month)">{{ 'grsia.pay' | t }}</button>
            </div>
          } @else if (!g.allPosted) {
            <p class="secondary">{{ 'grsia.notPosted' | t }}</p>
          }
        }
      </mat-tab>
    </mat-tab-group>
  `,
  styles: `
    .tab-body { margin-block-start: 16px; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 12px; }
    .compact td, .compact th { padding: 6px 8px; white-space: nowrap; }
    th.ded { color: #8c1d18; }
    .stats { display: grid; grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); gap: 12px; margin-bottom: 16px; }
    .stat { padding: 14px; }
    .stat .label { color: var(--mat-sys-on-surface-variant); }
    .stat .value { font: var(--mat-sys-headline-small); font-weight: 700; color: var(--mat-sys-primary); }
    .two { display: grid; grid-template-columns: repeat(auto-fit, minmax(320px, 1fr)); gap: 16px; }
    .reconcile { padding: 12px 16px; border-radius: 12px; background: #d8f3e5; color: #0b5a35; margin-block-end: 12px; }
    .reconcile.bad { background: #ffdcd9; color: #8c1d18; }
  `
})
export class HrReportsPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  private readonly i18n = inject(I18nService);
  protected readonly runs = signal<PayrollRun[]>([]);
  protected readonly runId = new FormControl('', { nonNullable: true });
  protected readonly asOf = new FormControl(todayIso(), { nonNullable: true });
  protected readonly year = new FormControl(new Date().getFullYear(), { nonNullable: true });
  protected readonly register = signal<Register | null>(null);
  protected readonly headcount = signal<Headcount | null>(null);
  protected readonly leave = signal<LeaveLiability | null>(null);
  protected readonly wps = signal<WpsRow[]>([]);
  protected readonly grsiaYear = signal<GrsiaMonth[]>([]);
  protected readonly grsia = signal<GrsiaSchedule | null>(null);
  protected readonly bankAccounts = signal<Account[]>([]);
  protected readonly canPay = inject(AuthService).can('hr.payroll:post');
  protected readonly payAccount = new FormControl('', { nonNullable: true });
  protected readonly payDate = new FormControl(todayIso(), { nonNullable: true });
  protected readonly payRef = new FormControl('', { nonNullable: true });

  ngOnInit(): void {
    this.api.payrollRuns().subscribe((runs) => {
      const sorted = [...runs].filter((r) => r.status !== 'Reversed' && r.status !== 'Draft').sort((a, b) => b.year - a.year || b.month - a.month);
      this.runs.set(sorted);
      if (sorted.length) {
        this.runId.setValue(sorted[0].id);
        this.loadRegister();
      }
    });
  }

  protected select(index: number): void {
    if (index === 1 && !this.headcount()) this.loadHeadcount();
    if (index === 2 && !this.leave()) this.loadLeave();
    if (index === 3 && !this.wps().length) this.loadWps();
    if (index === 4 && !this.grsiaYear().length) this.loadGrsiaYear();
  }

  protected period(month: number): string {
    return `${this.year.value}-${String(month).padStart(2, '0')}`;
  }

  protected loadGrsiaYear(): void {
    this.grsia.set(null);
    this.api.get<GrsiaMonth[]>('hr/reports/grsia/year', { year: this.year.value }).subscribe({ next: (r) => this.grsiaYear.set(r), error: (e) => this.notify.error(e) });
  }

  protected loadGrsia(month: number): void {
    this.api.get<GrsiaSchedule>('hr/reports/grsia', { year: this.year.value, month }).subscribe({ next: (r) => this.grsia.set(r), error: (e) => this.notify.error(e) });
    if (this.canPay && !this.bankAccounts().length)
      this.api.accounts().subscribe((a) => this.bankAccounts.set(a.filter((x) => x.isPostable && x.isActive && (x.subtype === 'bank' || x.subtype === 'cash'))));
  }

  protected grsiaExcel(year: number, month: number): void {
    this.api.download('hr/reports/grsia/xlsx', { year, month }, `grsia_${year}_${month}.xlsx`).subscribe({ error: (e) => this.notify.error(e) });
  }

  protected payGrsia(year: number, month: number): void {
    if (!confirm(this.i18n.t('grsia.payConfirm'))) return;
    this.api.post('hr/reports/grsia/pay', { year, month, accountId: this.payAccount.value, paymentDate: this.payDate.value, reference: this.payRef.value || null }).subscribe({
      next: () => {
        this.notify.success();
        this.loadGrsia(month);
        this.api.get<GrsiaMonth[]>('hr/reports/grsia/year', { year }).subscribe((r) => this.grsiaYear.set(r));
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected loadRegister(): void {
    if (!this.runId.value) return;
    this.api.get<Register>('hr/reports/payroll-register', { runId: this.runId.value }).subscribe({ next: (r) => this.register.set(r), error: (e) => this.notify.error(e) });
  }

  protected excel(): void {
    this.api.download('hr/reports/payroll-register.xlsx', { runId: this.runId.value }, 'payroll_register.xlsx').subscribe({ error: (e) => this.notify.error(e) });
  }

  protected loadHeadcount(): void {
    this.api.get<Headcount>('hr/reports/headcount', { asOf: this.asOf.value }).subscribe({ next: (r) => this.headcount.set(r), error: (e) => this.notify.error(e) });
  }

  protected loadLeave(): void {
    this.api.get<LeaveLiability>('hr/reports/leave-liability', { asOf: this.asOf.value }).subscribe({ next: (r) => this.leave.set(r), error: (e) => this.notify.error(e) });
  }

  protected loadWps(): void {
    this.api.get<WpsRow[]>('hr/reports/wps-reconciliation', { year: this.year.value }).subscribe({ next: (r) => this.wps.set(r), error: (e) => this.notify.error(e) });
  }

  protected nationality(code: string): string {
    const n = NATIONALITIES.find((x) => x.code === code);
    return n ? `${n.en} · ${n.ar}` : code;
  }
}
