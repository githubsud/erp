import { Component, inject, signal } from '@angular/core';
import { FormBuilder } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { toIso } from '../../core/dates';
import { TrialBalance } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

@Component({
  selector: 'app-trial-balance',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'nav.trialBalance' | t }}</h1>
      @if (tb(); as t) {
        <span class="chip" [class.ok]="t.isBalanced" [class.bad]="!t.isBalanced">{{ (t.isBalanced ? 'finance.balanced' : 'finance.notBalanced') | t }}</span>
      }
    </div>
    <form class="toolbar-row" [formGroup]="range" (ngSubmit)="load()">
      <mat-form-field>
        <mat-label>{{ 'app.from' | t }}</mat-label>
        <input matInput type="date" formControlName="from" />
      </mat-form-field>
      <mat-form-field>
        <mat-label>{{ 'app.to' | t }}</mat-label>
        <input matInput type="date" formControlName="to" />
      </mat-form-field>
      <button mat-flat-button>{{ 'finance.run' | t }}</button>
      <button mat-stroked-button type="button" (click)="exportCsv()"><mat-icon>download</mat-icon> {{ 'finance.exportCsv' | t }}</button>
    </form>

    @if (tb(); as t) {
      <div class="table-wrap">
        <table mat-table [dataSource]="t.rows">
          <ng-container matColumnDef="code">
            <th mat-header-cell *matHeaderCellDef>{{ 'finance.code' | t }}</th>
            <td mat-cell *matCellDef="let r" class="ltr">{{ r.code }}</td>
            <td mat-footer-cell *matFooterCellDef>{{ 'app.total' | t }}</td>
          </ng-container>
          <ng-container matColumnDef="name">
            <th mat-header-cell *matHeaderCellDef>{{ 'finance.account' | t }}</th>
            <td mat-cell *matCellDef="let r">{{ r | bi }}</td>
            <td mat-footer-cell *matFooterCellDef></td>
          </ng-container>
          <ng-container matColumnDef="opening">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'finance.opening' | t }}</th>
            <td mat-cell *matCellDef="let r" class="num ltr">{{ r.opening | money }}</td>
            <td mat-footer-cell *matFooterCellDef class="num ltr">{{ t.totalOpening | money }}</td>
          </ng-container>
          <ng-container matColumnDef="debit">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'finance.debit' | t }}</th>
            <td mat-cell *matCellDef="let r" class="num ltr">{{ r.debit | money }}</td>
            <td mat-footer-cell *matFooterCellDef class="num ltr">{{ t.totalDebit | money }}</td>
          </ng-container>
          <ng-container matColumnDef="credit">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'finance.credit' | t }}</th>
            <td mat-cell *matCellDef="let r" class="num ltr">{{ r.credit | money }}</td>
            <td mat-footer-cell *matFooterCellDef class="num ltr">{{ t.totalCredit | money }}</td>
          </ng-container>
          <ng-container matColumnDef="closing">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'finance.closing' | t }}</th>
            <td mat-cell *matCellDef="let r" class="num ltr">{{ r.closing | money }}</td>
            <td mat-footer-cell *matFooterCellDef class="num ltr">{{ t.totalClosing | money }}</td>
          </ng-container>
          <tr mat-header-row *matHeaderRowDef="columns"></tr>
          <tr mat-row *matRowDef="let row; columns: columns"></tr>
          <tr mat-footer-row *matFooterRowDef="columns"></tr>
        </table>
        @if (t.rows.length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
      </div>
    }
  `
})
export class TrialBalancePage {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly columns = ['code', 'name', 'opening', 'debit', 'credit', 'closing'];
  protected readonly tb = signal<TrialBalance | null>(null);
  private readonly now = new Date();
  protected readonly range = inject(FormBuilder).nonNullable.group({
    from: [toIso(new Date(this.now.getFullYear(), this.now.getMonth(), 1))],
    to: [toIso(new Date(this.now.getFullYear(), this.now.getMonth() + 1, 0))]
  });

  constructor() {
    this.load();
  }

  load(): void {
    const { from, to } = this.range.getRawValue();
    this.api.trialBalance(from, to).subscribe({ next: (t) => this.tb.set(t), error: (e) => this.notify.error(e) });
  }

  exportCsv(): void {
    const { from, to } = this.range.getRawValue();
    this.api.download('finance/exports/journal.csv', { from, to }, 'journal.csv').subscribe({ error: (e) => this.notify.error(e) });
  }
}
