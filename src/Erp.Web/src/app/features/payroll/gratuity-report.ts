import { Component, computed, inject, signal } from '@angular/core';
import { FormControl } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { todayIso } from '../../core/dates';
import { GratuityRow } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

@Component({
  selector: 'app-gratuity-report',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'gratuity.titleAll' | t }}</h1>
      <mat-form-field class="date">
        <mat-label>{{ 'gratuity.asOf' | t }}</mat-label>
        <input matInput type="date" [formControl]="asOf" />
      </mat-form-field>
      <button mat-flat-button (click)="load()">{{ 'finance.run' | t }}</button>
    </div>
    <div class="table-wrap">
      <table mat-table [dataSource]="rows()">
        <ng-container matColumnDef="employee">
          <th mat-header-cell *matHeaderCellDef>{{ 'employee.name' | t }}</th>
          <td mat-cell *matCellDef="let r"><a [routerLink]="['/employees', r.employeeId]"><span class="ltr">{{ r.empNo }}</span> · {{ r | bi }}</a></td>
          <td mat-footer-cell *matFooterCellDef>{{ 'app.total' | t }}</td>
        </ng-container>
        <ng-container matColumnDef="joining">
          <th mat-header-cell *matHeaderCellDef>{{ 'employee.joiningDate' | t }}</th>
          <td mat-cell *matCellDef="let r"><span class="ltr">{{ r.joiningDate | d }}</span></td>
          <td mat-footer-cell *matFooterCellDef></td>
        </ng-container>
        <ng-container matColumnDef="basis">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'gratuity.basis' | t }}</th>
          <td mat-cell *matCellDef="let r" class="num ltr">{{ r.basisWage | money }}</td>
          <td mat-footer-cell *matFooterCellDef></td>
        </ng-container>
        <ng-container matColumnDef="days">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'gratuity.serviceDays' | t }}</th>
          <td mat-cell *matCellDef="let r" class="num">{{ r.serviceDays }}</td>
          <td mat-footer-cell *matFooterCellDef></td>
        </ng-container>
        <ng-container matColumnDef="liability">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'gratuity.liability' | t }}</th>
          <td mat-cell *matCellDef="let r" class="num ltr">{{ r.liability | money }}</td>
          <td mat-footer-cell *matFooterCellDef class="num ltr">{{ totals().liability | money }}</td>
        </ng-container>
        <ng-container matColumnDef="provision">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'gratuity.provision' | t }}</th>
          <td mat-cell *matCellDef="let r" class="num ltr" [class.diff]="r.provision !== r.liability">{{ r.provision | money }}</td>
          <td mat-footer-cell *matFooterCellDef class="num ltr">{{ totals().provision | money }}</td>
        </ng-container>
        <ng-container matColumnDef="leave">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'gratuity.leave' | t }}</th>
          <td mat-cell *matCellDef="let r" class="num ltr">{{ r.leaveProvision | money }}</td>
          <td mat-footer-cell *matFooterCellDef class="num ltr">{{ totals().leave | money }}</td>
        </ng-container>
        <ng-container matColumnDef="ticket">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'gratuity.ticket' | t }}</th>
          <td mat-cell *matCellDef="let r" class="num ltr">{{ r.ticketProvision | money }}</td>
          <td mat-footer-cell *matFooterCellDef class="num ltr">{{ totals().ticket | money }}</td>
        </ng-container>
        <ng-container matColumnDef="eligible">
          <th mat-header-cell *matHeaderCellDef>{{ 'gratuity.eligible' | t }}</th>
          <td mat-cell *matCellDef="let r"><span class="chip" [class.ok]="r.eligibleForPayout">{{ (r.eligibleForPayout ? 'app.yes' : 'app.no') | t }}</span></td>
          <td mat-footer-cell *matFooterCellDef></td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
        <tr mat-footer-row *matFooterRowDef="columns"></tr>
      </table>
      @if (rows().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `,
  styles: `
    .date { width: 200px; }
    .diff { color: #7a4a00; }
    a { color: inherit; }
  `
})
export class GratuityReportPage {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly columns = ['employee', 'joining', 'basis', 'days', 'liability', 'provision', 'leave', 'ticket', 'eligible'];
  protected readonly asOf = new FormControl(todayIso(), { nonNullable: true });
  protected readonly rows = signal<GratuityRow[]>([]);
  protected readonly totals = computed(() => ({
    liability: this.rows().reduce((s, r) => s + r.liability, 0),
    provision: this.rows().reduce((s, r) => s + r.provision, 0),
    leave: this.rows().reduce((s, r) => s + r.leaveProvision, 0),
    ticket: this.rows().reduce((s, r) => s + r.ticketProvision, 0)
  }));

  constructor() {
    this.load();
  }

  load(): void {
    this.api.gratuityLiability(this.asOf.value).subscribe({ next: (r) => this.rows.set(r), error: (e) => this.notify.error(e) });
  }
}
