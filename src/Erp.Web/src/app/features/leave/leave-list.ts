import { Component, inject, signal } from '@angular/core';
import { FormControl } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { forkJoin } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { Employee, LeaveRequest, LeaveType } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

@Component({
  selector: 'app-leave-list',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'leave.title' | t }}</h1>
      <mat-form-field class="status">
        <mat-label>{{ 'app.status' | t }}</mat-label>
        <mat-select [formControl]="status">
          <mat-option value="">{{ 'employee.allStatuses' | t }}</mat-option>
          @for (s of statuses; track s) { <mat-option [value]="s">{{ 'leave.status.' + s | t }}</mat-option> }
        </mat-select>
      </mat-form-field>
    </div>
    <div class="table-wrap">
      <table mat-table [dataSource]="requests()">
        <ng-container matColumnDef="employee">
          <th mat-header-cell *matHeaderCellDef>{{ 'inputs.employee' | t }}</th>
          <td mat-cell *matCellDef="let l"><a [routerLink]="['/employees', l.employeeId]" [queryParams]="{ tab: 'leave' }">{{ employeeName(l.employeeId) }}</a></td>
        </ng-container>
        <ng-container matColumnDef="type">
          <th mat-header-cell *matHeaderCellDef>{{ 'leave.type' | t }}</th>
          <td mat-cell *matCellDef="let l">{{ typeName(l.leaveTypeCode) }}</td>
        </ng-container>
        <ng-container matColumnDef="dates">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.date' | t }}</th>
          <td mat-cell *matCellDef="let l"><span class="ltr">{{ l.fromDate | d }} → {{ l.toDate | d }}</span></td>
        </ng-container>
        <ng-container matColumnDef="days">
          <th mat-header-cell *matHeaderCellDef>{{ 'leave.days' | t }}</th>
          <td mat-cell *matCellDef="let l">{{ l.days }}</td>
        </ng-container>
        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
          <td mat-cell *matCellDef="let l">
            <span class="chip" [class.ok]="l.status === 'Approved'" [class.warn]="l.status === 'Pending'" [class.bad]="l.status === 'Rejected'">{{ 'leave.status.' + l.status | t }}</span>
          </td>
        </ng-container>
        <ng-container matColumnDef="actions">
          <th mat-header-cell *matHeaderCellDef></th>
          <td mat-cell *matCellDef="let l">
            @if (l.status === 'Pending' && auth.can('hr.leave:approve')) {
              <button mat-button (click)="decide(l, 'approve')">{{ 'leave.approve' | t }}</button>
              <button mat-button (click)="decide(l, 'reject')">{{ 'leave.reject' | t }}</button>
            }
          </td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
      </table>
      @if (requests().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `,
  styles: `.status { width: 200px; } a { color: inherit; }`
})
export class LeaveListPage {
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  private readonly i18n = inject(I18nService);

  protected readonly statuses = ['Pending', 'Approved', 'Rejected', 'Cancelled'];
  protected readonly columns = ['employee', 'type', 'dates', 'days', 'status', 'actions'];
  protected readonly status = new FormControl('Pending', { nonNullable: true });
  protected readonly requests = signal<LeaveRequest[]>([]);
  private readonly employees = signal<Employee[]>([]);
  private readonly types = signal<LeaveType[]>([]);

  constructor() {
    forkJoin({ e: this.api.employees({ pageSize: 200 }), t: this.api.leaveTypes() }).subscribe(({ e, t }) => {
      this.employees.set(e.items);
      this.types.set(t);
    });
    this.status.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => this.load());
    this.load();
  }

  protected employeeName(id: string): string {
    const e = this.employees().find((x) => x.id === id);
    return e ? `${e.empNo} · ${this.i18n.pick(e)}` : '…';
  }

  protected typeName(code: string): string {
    const t = this.types().find((x) => x.code === code);
    return t ? this.i18n.pick(t) : code;
  }

  protected decide(l: LeaveRequest, action: 'approve' | 'reject'): void {
    this.api.decideLeave(l.id, action).subscribe({
      next: () => {
        this.notify.success();
        this.load();
      },
      error: (e) => this.notify.error(e)
    });
  }

  private load(): void {
    this.api.leaveRequests({ status: this.status.value }).subscribe((r) => this.requests.set(r));
  }
}
