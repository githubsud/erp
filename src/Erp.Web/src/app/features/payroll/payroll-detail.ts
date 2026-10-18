import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { MatExpansionModule } from '@angular/material/expansion';
import { Observable, filter, switchMap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { daysBetween, todayIso } from '../../core/dates';
import { I18nService } from '../../core/i18n/i18n.service';
import { PayrollRun, PayrollRunDetail, Payslip, PayslipSummary, WpsFile } from '../../core/models';
import { NotifyService, problemErrors } from '../../core/notify.service';
import { ConfirmDialog, PaymentDialog, PaymentResult, ReverseDialog, ReverseResult } from '../../shared/dialogs';
import { SHARED } from '../../shared/shared';
import { MONTHS_AR, MONTHS_EN, statusClass } from './payroll-list';

@Component({
  selector: 'app-payroll-detail',
  imports: [...SHARED, MatExpansionModule],
  template: `
    @if (detail(); as d) {
      <div class="page-header">
        <a mat-icon-button routerLink="/payroll"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
        <div class="title">
          <h1>{{ monthName(d.run) }}</h1>
          <div class="sub"><span class="ltr">{{ d.run.runNo }}</span> · <span class="chip" [class]="statusClass(d.run.status)">{{ 'payroll.status.' + d.run.status | t }}</span></div>
        </div>
        <div class="actions">
          @switch (d.run.status) {
            @case ('Draft') {
              @if (auth.can('hr.payroll:prepare')) { <button mat-flat-button (click)="act('calculate')" [disabled]="busy()"><mat-icon>calculate</mat-icon> {{ 'payroll.calculate' | t }}</button> }
            }
            @case ('Calculated') {
              @if (auth.can('hr.payroll:prepare')) { <button mat-stroked-button (click)="act('calculate')" [disabled]="busy()"><mat-icon>refresh</mat-icon> {{ 'payroll.recalculate' | t }}</button> }
              @if (auth.can('hr.payroll:approve')) { <button mat-flat-button (click)="act('approve')" [disabled]="busy()"><mat-icon>verified</mat-icon> {{ 'payroll.approve' | t }}</button> }
            }
            @case ('Approved') {
              @if (auth.can('hr.payroll:post')) { <button mat-flat-button (click)="act('post')" [disabled]="busy()"><mat-icon>account_balance</mat-icon> {{ 'payroll.post' | t }}</button> }
            }
            @case ('Posted') {
              @if (auth.can('hr.payroll:post')) {
                <button mat-flat-button (click)="generateWps()" [disabled]="busy()"><mat-icon>description</mat-icon> {{ 'payroll.generateWps' | t }}</button>
                <button mat-stroked-button (click)="markPaid()" [disabled]="busy()"><mat-icon>payments</mat-icon> {{ 'payroll.markPaid' | t }}</button>
                <button mat-button (click)="reverse()" [disabled]="busy()"><mat-icon>undo</mat-icon> {{ 'payroll.reverse' | t }}</button>
              }
            }
          }
          @if (d.payslips.length && (d.run.status === 'Posted' || d.run.status === 'Paid') && auth.can('hr.payroll:post')) {
            <button mat-stroked-button (click)="emailPayslips()" [disabled]="busy()"><mat-icon>forward_to_inbox</mat-icon> {{ 'payroll.emailPayslips' | t }}</button>
          }
          @if (d.payslips.length) {
            <button mat-stroked-button (click)="allPayslips()"><mat-icon>picture_as_pdf</mat-icon> {{ 'payroll.payslipsPdf' | t }}</button>
          }
        </div>
      </div>
      @if (busy()) { <mat-progress-bar mode="indeterminate" /> }

      @if (errors().length) {
        <div class="error-panel">
          <strong>{{ 'payroll.errors' | t }}</strong>
          <ul>@for (e of errors(); track $index) { <li>{{ e }}</li> }</ul>
        </div>
      }

      <div class="card-grid">
        <mat-card appearance="outlined" class="stat">
          <div class="label">{{ 'payroll.employees' | t }}</div>
          <div class="value">{{ d.run.employeeCount }}</div>
        </mat-card>
        <mat-card appearance="outlined" class="stat">
          <div class="label">{{ 'payroll.gross' | t }}</div>
          <div class="value ltr">{{ d.run.totalGross | money }}</div>
          <div class="hint">{{ 'payroll.deductions' | t }}: <span class="ltr">{{ d.run.totalDeductions | money }}</span></div>
        </mat-card>
        <mat-card appearance="outlined" class="stat net">
          <div class="label">{{ 'payroll.net' | t }}</div>
          <div class="value ltr">{{ d.run.totalNet | money }}</div>
          <div class="hint">{{ 'app.qar' | t }}</div>
        </mat-card>
        <mat-card appearance="outlined" class="stat">
          <div class="label">{{ 'payroll.wpsDeadline' | t }}</div>
          <div class="value ltr">{{ d.run.wpsDeadline | d }}</div>
          <div class="hint">
            {{ 'payroll.dueDate' | t }}: <span class="ltr">{{ d.run.dueDate | d }}</span>
            @if (d.run.status !== 'Paid' && d.run.status !== 'Reversed') {
              · @if (deadlineDays() >= 0) { {{ 'dashboard.daysLeft' | t: { n: deadlineDays() } }} } @else { <b class="late">{{ 'dashboard.overdue' | t: { n: -deadlineDays() } }}</b> }
            }
          </div>
        </mat-card>
        @if (d.run.totalGratuityAccrual || d.run.totalLeaveAccrual || d.run.totalTicketAccrual) {
          <mat-card appearance="outlined" class="stat">
            <div class="label">{{ 'payroll.provisions' | t }}</div>
            <div class="value ltr">{{ d.run.totalGratuityAccrual + d.run.totalLeaveAccrual + d.run.totalTicketAccrual | money }}</div>
            <div class="hint">
              {{ 'payroll.gratuity' | t }} <span class="ltr">{{ d.run.totalGratuityAccrual | money }}</span> ·
              {{ 'gratuity.leave' | t }} <span class="ltr">{{ d.run.totalLeaveAccrual | money }}</span> ·
              {{ 'gratuity.ticket' | t }} <span class="ltr">{{ d.run.totalTicketAccrual | money }}</span>
            </div>
          </mat-card>
        }
      </div>

      @if (wpsFiles().length) {
        <div class="section-title">{{ 'payroll.wpsFiles' | t }}</div>
        <div class="wps">
          @for (f of wpsFiles(); track f.id) {
            <mat-card appearance="outlined" class="wps-file">
              <mat-icon>description</mat-icon>
              <div class="grow">
                <div class="ltr">{{ f.fileName }}</div>
                <div class="hint">{{ 'payroll.records' | t }}: {{ f.recordCount }} · <span class="ltr">{{ f.totalSalaries | money }}</span></div>
              </div>
              <button mat-icon-button (click)="downloadWps(f)" [matTooltip]="'app.download' | t"><mat-icon>download</mat-icon></button>
            </mat-card>
          }
        </div>
      }

      <div class="table-wrap">
        <table mat-table [dataSource]="d.payslips">
          <ng-container matColumnDef="empNo">
            <th mat-header-cell *matHeaderCellDef>{{ 'employee.empNo' | t }}</th>
            <td mat-cell *matCellDef="let p" class="ltr">{{ p.empNo }}</td>
            <td mat-footer-cell *matFooterCellDef>{{ 'app.total' | t }}</td>
          </ng-container>
          <ng-container matColumnDef="name">
            <th mat-header-cell *matHeaderCellDef>{{ 'employee.name' | t }}</th>
            <td mat-cell *matCellDef="let p">
              {{ p | bi }}
              @if (p.warnings) { <mat-icon class="warn-icon" [matTooltip]="p.warnings">warning</mat-icon> }
              @if (!p.inWps) { <span class="chip">WPS ✕</span> }
              @if (p.emailedAt) { <mat-icon class="mail-icon" [matTooltip]="('payroll.emailed' | t) + ' ' + (p.emailedAt | d)">mark_email_read</mat-icon> }
            </td>
            <td mat-footer-cell *matFooterCellDef></td>
          </ng-container>
          <ng-container matColumnDef="days">
            <th mat-header-cell *matHeaderCellDef>{{ 'payroll.paidDays' | t }}</th>
            <td mat-cell *matCellDef="let p">{{ p.paidDays }}@if (p.unpaidDays) { <span class="secondary"> (−{{ p.unpaidDays }})</span> }@if (p.halfPayDays) { <span class="secondary" [matTooltip]="'payroll.halfPay' | t"> ½×{{ p.halfPayDays }}</span> }</td>
            <td mat-footer-cell *matFooterCellDef></td>
          </ng-container>
          <ng-container matColumnDef="gross">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'payroll.gross' | t }}</th>
            <td mat-cell *matCellDef="let p" class="num ltr">{{ p.gross | money }}</td>
            <td mat-footer-cell *matFooterCellDef class="num ltr">{{ d.run.totalGross | money }}</td>
          </ng-container>
          <ng-container matColumnDef="deductions">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'payroll.deductions' | t }}</th>
            <td mat-cell *matCellDef="let p" class="num ltr">{{ p.deductions | money }}</td>
            <td mat-footer-cell *matFooterCellDef class="num ltr">{{ d.run.totalDeductions | money }}</td>
          </ng-container>
          <ng-container matColumnDef="net">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'payroll.net' | t }}</th>
            <td mat-cell *matCellDef="let p" class="num ltr"><b>{{ p.net | money }}</b></td>
            <td mat-footer-cell *matFooterCellDef class="num ltr">{{ d.run.totalNet | money }}</td>
          </ng-container>
          <ng-container matColumnDef="actions">
            <th mat-header-cell *matHeaderCellDef></th>
            <td mat-cell *matCellDef="let p">
              <button mat-icon-button (click)="$event.stopPropagation(); payslipPdf(p)" [matTooltip]="'payroll.payslip' | t"><mat-icon>picture_as_pdf</mat-icon></button>
            </td>
            <td mat-footer-cell *matFooterCellDef></td>
          </ng-container>
          <tr mat-header-row *matHeaderRowDef="columns"></tr>
          <tr mat-row *matRowDef="let row; columns: columns" class="clickable" (click)="toggle(row)"></tr>
          <tr mat-footer-row *matFooterRowDef="columns"></tr>
        </table>
        @if (d.payslips.length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
      </div>

      @if (selected(); as p) {
        <mat-card appearance="outlined" class="payslip">
          <div class="section-title">{{ 'payroll.payslip' | t }} · {{ p.summary | bi }}</div>
          <table class="lines">
            @for (l of p.lines; track l.lineNo) {
              <tr [class.deduction]="l.kind === 'Deduction'">
                <td>{{ l | bi }}</td>
                <td class="ltr">{{ l.quantity ?? '' }}</td>
                <td class="num ltr">{{ l.kind === 'Deduction' ? '−' : '' }}{{ l.amount | money }}</td>
              </tr>
            }
            <tr class="total"><td>{{ 'payroll.net' | t }}</td><td></td><td class="num ltr">{{ p.summary.net | money }}</td></tr>
          </table>
        </mat-card>
      }
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: `
    .title { flex: 1; h1 { margin: 0; } }
    .net .value { color: var(--mat-sys-primary); }
    .late { color: #8c1d18; }
    .wps { display: grid; gap: 12px; grid-template-columns: repeat(auto-fill, minmax(320px, 1fr)); margin-bottom: 16px; }
    .wps-file { display: flex; flex-direction: row; align-items: center; gap: 12px; padding: 12px 16px; }
    .grow { flex: 1; min-width: 0; overflow-wrap: anywhere; }
    .hint { font-size: 12px; color: var(--mat-sys-on-surface-variant); }
    .secondary { color: var(--mat-sys-on-surface-variant); }
    .mail-icon { color: var(--mat-sys-primary); font-size: 18px; width: 18px; height: 18px; vertical-align: middle; }
    .warn-icon { color: #b26a00; font-size: 18px; width: 18px; height: 18px; vertical-align: middle; }
    .payslip { margin-top: 16px; padding: 8px 20px 16px; }
    .lines { width: min(560px, 100%); border-collapse: collapse; }
    .lines td { padding: 6px 0; border-bottom: 1px solid var(--mat-sys-outline-variant); }
    .lines td.num { padding-inline-start: 24px; }
    .deduction td { color: #8c1d18; }
    .total td { font-weight: 700; border: none; }
  `
})
export class PayrollDetailPage implements OnInit {
  readonly id = input.required<string>();
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  private readonly dialog = inject(MatDialog);
  private readonly i18n = inject(I18nService);

  protected readonly statusClass = statusClass;
  protected readonly columns = ['empNo', 'name', 'days', 'gross', 'deductions', 'net', 'actions'];
  protected readonly detail = signal<PayrollRunDetail | null>(null);
  protected readonly wpsFiles = signal<WpsFile[]>([]);
  protected readonly selected = signal<Payslip | null>(null);
  protected readonly busy = signal(false);
  protected readonly errors = signal<string[]>([]);
  protected readonly deadlineDays = computed(() => (this.detail() ? daysBetween(todayIso(), this.detail()!.run.wpsDeadline) : 0));

  ngOnInit(): void {
    this.load();
  }

  protected monthName(r: PayrollRun): string {
    return `${(this.i18n.isArabic() ? MONTHS_AR : MONTHS_EN)[r.month - 1]} ${r.year}`;
  }

  protected act(action: 'calculate' | 'approve' | 'post'): void {
    this.run(this.api.payrollAction(this.id(), action));
  }

  protected generateWps(): void {
    this.busy.set(true);
    this.errors.set([]);
    this.api.generateWps(this.id()).subscribe({
      next: (f) => {
        this.busy.set(false);
        this.notify.success();
        this.loadWps();
        this.downloadWps(f);
      },
      error: (e) => this.fail(e)
    });
  }

  protected markPaid(): void {
    this.dialog
      .open(PaymentDialog, { data: { title: 'payroll.markPaid', date: this.detail()!.run.periodEnd > todayIso() ? this.detail()!.run.periodEnd : todayIso() } })
      .afterClosed()
      .pipe(filter((r): r is PaymentResult => !!r))
      .subscribe((r) => this.run(this.api.markPayrollPaid(this.id(), r.paymentAccountId, r.paymentDate)));
  }

  protected reverse(): void {
    this.dialog
      .open(ReverseDialog, { data: { date: this.detail()!.run.periodEnd, warning: 'payroll.reverseConfirm' } })
      .afterClosed()
      .pipe(filter((r): r is ReverseResult => !!r))
      .subscribe((r) => this.run(this.api.reversePayroll(this.id(), r.date, r.reason)));
  }

  protected toggle(p: PayslipSummary): void {
    if (this.selected()?.summary.id === p.id) {
      this.selected.set(null);
      return;
    }
    this.api.payslip(this.id(), p.id).subscribe((s) => this.selected.set(s));
  }

  protected payslipPdf(p: PayslipSummary): void {
    this.api.download(`hr/payroll-runs/${this.id()}/payslips/${p.id}/pdf`).subscribe({ error: (e) => this.notify.error(e) });
  }

  protected emailPayslips(resend = false): void {
    this.busy.set(true);
    this.api.emailPayslips(this.id(), resend).subscribe({
      next: (r) => {
        this.busy.set(false);
        if (r.queued === 0 && r.alreadySent > 0 && !resend) {
          this.dialog
            .open(ConfirmDialog, { data: { title: 'payroll.emailPayslips', message: 'payroll.emailResend' } })
            .afterClosed()
            .subscribe((ok) => ok && this.emailPayslips(true));
          return;
        }
        this.notify.info(this.i18n.t('payroll.emailQueued', { queued: r.queued, missing: r.withoutEmail }));
        this.load();
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }

  protected allPayslips(): void {
    this.api.download(`hr/payroll-runs/${this.id()}/payslips.pdf`).subscribe({ error: (e) => this.notify.error(e) });
  }

  protected downloadWps(f: WpsFile): void {
    this.api.download(`hr/wps-files/${f.id}/download`, undefined, f.fileName).subscribe({ error: (e) => this.notify.error(e) });
  }

  private run(request: Observable<PayrollRun>): void {
    this.busy.set(true);
    this.errors.set([]);
    request.pipe(switchMap(() => this.api.payrollRun(this.id()))).subscribe({
      next: (d) => {
        this.busy.set(false);
        this.detail.set(d);
        this.selected.set(null);
        this.notify.success();
        this.loadWps();
      },
      error: (e) => this.fail(e)
    });
  }

  private fail(e: unknown): void {
    this.busy.set(false);
    this.errors.set(problemErrors(e));
    this.notify.error(e);
  }

  private load(): void {
    this.api.payrollRun(this.id()).subscribe({ next: (d) => this.detail.set(d), error: (e) => this.notify.error(e) });
    this.loadWps();
  }

  private loadWps(): void {
    this.api.wpsFiles(this.id()).subscribe((f) => this.wpsFiles.set(f));
  }
}
