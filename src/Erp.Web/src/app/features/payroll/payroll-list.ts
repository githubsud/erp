import { Component, inject, signal } from '@angular/core';
import { FormControl, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { PayrollRun } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

export const MONTHS_EN = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
export const MONTHS_AR = ['يناير', 'فبراير', 'مارس', 'أبريل', 'مايو', 'يونيو', 'يوليو', 'أغسطس', 'سبتمبر', 'أكتوبر', 'نوفمبر', 'ديسمبر'];

export function statusClass(status: string): Record<string, boolean> {
  return { ok: status === 'Paid', info: status === 'Posted' || status === 'Approved', warn: status === 'Draft' || status === 'Calculated', bad: status === 'Reversed' };
}

@Component({
  selector: 'app-payroll-list',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'payroll.title' | t }}</h1>
      @if (auth.can('hr.payroll:prepare')) {
        <mat-form-field class="month">
          <mat-label>{{ 'payroll.month' | t }}</mat-label>
          <input matInput type="month" [formControl]="month" />
        </mat-form-field>
        <button mat-flat-button (click)="create()" [disabled]="month.invalid || busy()"><mat-icon>add</mat-icon> {{ 'payroll.new' | t }}</button>
      }
    </div>

    <div class="table-wrap">
      <table mat-table [dataSource]="runs()">
        <ng-container matColumnDef="month">
          <th mat-header-cell *matHeaderCellDef>{{ 'payroll.month' | t }}</th>
          <td mat-cell *matCellDef="let r">{{ monthName(r) }}</td>
        </ng-container>
        <ng-container matColumnDef="runNo">
          <th mat-header-cell *matHeaderCellDef>{{ 'payroll.runNo' | t }}</th>
          <td mat-cell *matCellDef="let r" class="ltr">{{ r.runNo }}</td>
        </ng-container>
        <ng-container matColumnDef="employees">
          <th mat-header-cell *matHeaderCellDef>{{ 'payroll.employees' | t }}</th>
          <td mat-cell *matCellDef="let r">{{ r.employeeCount }}</td>
        </ng-container>
        <ng-container matColumnDef="net">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'payroll.net' | t }}</th>
          <td mat-cell *matCellDef="let r" class="num ltr">{{ r.totalNet | money }}</td>
        </ng-container>
        <ng-container matColumnDef="deadline">
          <th mat-header-cell *matHeaderCellDef>{{ 'payroll.wpsDeadline' | t }}</th>
          <td mat-cell *matCellDef="let r"><span class="ltr">{{ r.wpsDeadline | d }}</span></td>
        </ng-container>
        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
          <td mat-cell *matCellDef="let r"><span class="chip" [class]="statusClass(r.status)">{{ 'payroll.status.' + r.status | t }}</span></td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns" class="clickable" (click)="open(row)"></tr>
      </table>
      @if (runs().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `,
  styles: `.month { width: 200px; }`
})
export class PayrollListPage {
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly notify = inject(NotifyService);
  private readonly i18n = inject(I18nService);

  protected readonly statusClass = statusClass;
  protected readonly columns = ['month', 'runNo', 'employees', 'net', 'deadline', 'status'];
  protected readonly runs = signal<PayrollRun[]>([]);
  protected readonly busy = signal(false);
  protected readonly month = new FormControl(currentMonth(), { nonNullable: true, validators: Validators.required });

  constructor() {
    this.api.payrollRuns().subscribe({ next: (r) => this.runs.set(r), error: (e) => this.notify.error(e) });
  }

  protected monthName(r: PayrollRun): string {
    return `${(this.i18n.isArabic() ? MONTHS_AR : MONTHS_EN)[r.month - 1]} ${r.year}`;
  }

  protected open(r: PayrollRun): void {
    void this.router.navigate(['/payroll', r.id]);
  }

  protected create(): void {
    const [year, month] = this.month.value.split('-').map(Number);
    this.busy.set(true);
    this.api.createPayrollRun(year, month).subscribe({
      next: (r) => void this.router.navigate(['/payroll', r.id]),
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }
}

export function currentMonth(): string {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
}
