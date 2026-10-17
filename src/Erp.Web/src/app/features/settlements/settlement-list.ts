import { Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { ApiService } from '../../core/api.service';
import { Settlement } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';
import { statusClass } from '../payroll/payroll-list';

@Component({
  selector: 'app-settlement-list',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'settlement.title' | t }}</h1></div>
    <div class="table-wrap">
      <table mat-table [dataSource]="rows()">
        <ng-container matColumnDef="no">
          <th mat-header-cell *matHeaderCellDef>#</th>
          <td mat-cell *matCellDef="let s" class="ltr">{{ s.settlementNo }}</td>
        </ng-container>
        <ng-container matColumnDef="employee">
          <th mat-header-cell *matHeaderCellDef>{{ 'inputs.employee' | t }}</th>
          <td mat-cell *matCellDef="let s"><span class="ltr">{{ s.empNo }}</span> · {{ s | bi }}</td>
        </ng-container>
        <ng-container matColumnDef="lastDay">
          <th mat-header-cell *matHeaderCellDef>{{ 'settlement.lastDay' | t }}</th>
          <td mat-cell *matCellDef="let s"><span class="ltr">{{ s.lastWorkingDay | d }}</span></td>
        </ng-container>
        <ng-container matColumnDef="reason">
          <th mat-header-cell *matHeaderCellDef>{{ 'settlement.reason' | t }}</th>
          <td mat-cell *matCellDef="let s">{{ 'settlement.reason.' + s.reason | t }}</td>
        </ng-container>
        <ng-container matColumnDef="net">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'settlement.net' | t }}</th>
          <td mat-cell *matCellDef="let s" class="num ltr">{{ s.netPayable | money }}</td>
        </ng-container>
        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
          <td mat-cell *matCellDef="let s"><span class="chip" [class]="statusClass(s.status)">{{ 'settlement.status.' + s.status | t }}</span></td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns" class="clickable" (click)="open(row)"></tr>
      </table>
      @if (rows().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `
})
export class SettlementListPage {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly notify = inject(NotifyService);
  protected readonly statusClass = statusClass;
  protected readonly columns = ['no', 'employee', 'lastDay', 'reason', 'net', 'status'];
  protected readonly rows = signal<Settlement[]>([]);

  constructor() {
    this.api.settlements().subscribe({ next: (r) => this.rows.set(r), error: (e) => this.notify.error(e) });
  }

  protected open(s: Settlement): void {
    void this.router.navigate(['/employees', s.employeeId], { queryParams: { tab: 'settlement' } });
  }
}
