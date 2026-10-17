import { Component, inject, input, OnInit, signal } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
import { ApiService } from '../../../core/api.service';
import { AuthService } from '../../../core/auth.service';
import { blankToNull } from '../../../core/dates';
import { Employee, LeaveBalance, LeaveRequest, LeaveType } from '../../../core/models';
import { NotifyService } from '../../../core/notify.service';
import { I18nService } from '../../../core/i18n/i18n.service';
import { SHARED } from '../../../shared/shared';

@Component({
  selector: 'app-leave-tab',
  imports: [...SHARED],
  template: `
    @if (balance(); as b) {
      <div class="card-grid">
        <mat-card appearance="outlined" class="stat">
          <div class="label">{{ 'leave.balance' | t }}</div>
          <div class="value ltr">{{ b.balance }}</div>
          <div class="hint">{{ 'leave.entitlement' | t }}: {{ b.entitlementDaysPerYear }} · {{ 'leave.accrued' | t }}: {{ b.accrued }} · {{ 'leave.taken' | t }}: {{ b.taken }}</div>
        </mat-card>
      </div>
    }

    @if (auth.can('hr.leave:manage') && employee().status === 'Active') {
      <form [formGroup]="form" (ngSubmit)="add()" class="add">
        <div class="form-grid">
          <mat-form-field>
            <mat-label>{{ 'leave.type' | t }}</mat-label>
            <mat-select formControlName="leaveTypeCode">
              @for (t of types(); track t.code) { <mat-option [value]="t.code">{{ t | bi }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'app.from' | t }}</mat-label>
            <input matInput type="date" formControlName="fromDate" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'app.to' | t }}</mat-label>
            <input matInput type="date" formControlName="toDate" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'app.reason' | t }}</mat-label>
            <input matInput formControlName="reason" />
          </mat-form-field>
        </div>
        <div class="actions"><button mat-flat-button [disabled]="form.invalid"><mat-icon>add</mat-icon> {{ 'leave.new' | t }}</button></div>
      </form>
    }

    <div class="table-wrap">
      <table mat-table [dataSource]="requests()">
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
            <span class="chip" [class.ok]="l.status === 'Approved'" [class.warn]="l.status === 'Pending'" [class.bad]="l.status === 'Rejected'">
              {{ 'leave.status.' + l.status | t }}
            </span>
          </td>
        </ng-container>
        <ng-container matColumnDef="actions">
          <th mat-header-cell *matHeaderCellDef></th>
          <td mat-cell *matCellDef="let l">
            @if (l.status === 'Pending' && auth.can('hr.leave:approve')) {
              <button mat-button (click)="decide(l, 'approve')">{{ 'leave.approve' | t }}</button>
              <button mat-button (click)="decide(l, 'reject')">{{ 'leave.reject' | t }}</button>
            }
            @if ((l.status === 'Pending' || l.status === 'Approved') && auth.can('hr.leave:manage')) {
              <button mat-button (click)="decide(l, 'cancel')">{{ 'leave.cancel' | t }}</button>
            }
          </td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
      </table>
      @if (requests().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `,
  styles: `.add { margin-bottom: 16px; }`
})
export class LeaveTab implements OnInit {
  readonly employee = input.required<Employee>();
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  private readonly i18n = inject(I18nService);

  protected readonly columns = ['type', 'dates', 'days', 'status', 'actions'];
  protected readonly types = signal<LeaveType[]>([]);
  protected readonly requests = signal<LeaveRequest[]>([]);
  protected readonly balance = signal<LeaveBalance | null>(null);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    leaveTypeCode: ['ANNUAL', Validators.required],
    fromDate: ['', Validators.required],
    toDate: ['', Validators.required],
    reason: ['']
  });

  ngOnInit(): void {
    this.api.leaveTypes().subscribe((t) => this.types.set(t));
    this.load();
  }

  protected typeName(code: string): string {
    const t = this.types().find((x) => x.code === code);
    return t ? this.i18n.pick(t) : code;
  }

  protected add(): void {
    this.api.createLeave({ employeeId: this.employee().id, ...blankToNull(this.form.getRawValue()) }).subscribe({
      next: () => {
        this.notify.success();
        this.form.reset({ leaveTypeCode: 'ANNUAL', fromDate: '', toDate: '', reason: '' });
        this.load();
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected decide(l: LeaveRequest, action: 'approve' | 'reject' | 'cancel'): void {
    this.api.decideLeave(l.id, action).subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  private load(): void {
    this.api.leaveRequests({ employeeId: this.employee().id }).subscribe((r) => this.requests.set(r));
    this.api.leaveBalance(this.employee().id).subscribe((b) => this.balance.set(b));
  }
}
