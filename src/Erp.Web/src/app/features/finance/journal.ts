import { Component, inject, signal } from '@angular/core';
import { FormBuilder } from '@angular/forms';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { ApiService } from '../../core/api.service';
import { toIso } from '../../core/dates';
import { JournalEntryDetail, JournalEntrySummary } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

@Component({
  selector: 'app-journal',
  imports: [...SHARED, MatPaginatorModule],
  template: `
    <div class="page-header"><h1>{{ 'nav.journal' | t }}</h1></div>
    <form class="toolbar-row" [formGroup]="range" (ngSubmit)="load(0)">
      <mat-form-field>
        <mat-label>{{ 'app.from' | t }}</mat-label>
        <input matInput type="date" formControlName="from" />
      </mat-form-field>
      <mat-form-field>
        <mat-label>{{ 'app.to' | t }}</mat-label>
        <input matInput type="date" formControlName="to" />
      </mat-form-field>
      <button mat-flat-button>{{ 'finance.run' | t }}</button>
    </form>

    <div class="table-wrap">
      <table mat-table [dataSource]="rows()">
        <ng-container matColumnDef="no">
          <th mat-header-cell *matHeaderCellDef>{{ 'finance.entryNo' | t }}</th>
          <td mat-cell *matCellDef="let e" class="ltr">{{ e.entryNo }} @if (e.reversalOfId) { <span class="chip bad">↺</span> }</td>
        </ng-container>
        <ng-container matColumnDef="date">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.date' | t }}</th>
          <td mat-cell *matCellDef="let e"><span class="ltr">{{ e.postingDate | d }}</span></td>
        </ng-container>
        <ng-container matColumnDef="source">
          <th mat-header-cell *matHeaderCellDef>{{ 'finance.source' | t }}</th>
          <td mat-cell *matCellDef="let e" class="ltr">{{ e.sourceType }} · {{ e.sourceNo }}</td>
        </ng-container>
        <ng-container matColumnDef="memo">
          <th mat-header-cell *matHeaderCellDef>{{ 'finance.memo' | t }}</th>
          <td mat-cell *matCellDef="let e">{{ e | bi:'memo' }}</td>
        </ng-container>
        <ng-container matColumnDef="total">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'app.amount' | t }}</th>
          <td mat-cell *matCellDef="let e" class="num ltr">{{ e.totalDebit | money }}</td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns" class="clickable" (click)="open(row)"></tr>
      </table>
      @if (rows().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
      <mat-paginator [length]="total()" [pageSize]="50" [pageSizeOptions]="[50, 100]" (page)="page($event)" />
    </div>

    @if (selected(); as s) {
      <mat-card appearance="outlined" class="detail">
        <div class="section-title"><span class="ltr">{{ s.entry.entryNo }}</span> · {{ s.entry | bi:'memo' }}</div>
        <div class="table-wrap">
          <table mat-table [dataSource]="s.lines">
            <ng-container matColumnDef="account">
              <th mat-header-cell *matHeaderCellDef>{{ 'finance.account' | t }}</th>
              <td mat-cell *matCellDef="let l"><span class="ltr">{{ l.accountCode }}</span> · {{ l | bi:'accountName' }}</td>
            </ng-container>
            <ng-container matColumnDef="description">
              <th mat-header-cell *matHeaderCellDef>{{ 'finance.memo' | t }}</th>
              <td mat-cell *matCellDef="let l">{{ l.description }}</td>
            </ng-container>
            <ng-container matColumnDef="debit">
              <th mat-header-cell *matHeaderCellDef class="num">{{ 'finance.debit' | t }}</th>
              <td mat-cell *matCellDef="let l" class="num ltr">{{ l.debitBase ? (l.debitBase | money) : '' }}</td>
            </ng-container>
            <ng-container matColumnDef="credit">
              <th mat-header-cell *matHeaderCellDef class="num">{{ 'finance.credit' | t }}</th>
              <td mat-cell *matCellDef="let l" class="num ltr">{{ l.creditBase ? (l.creditBase | money) : '' }}</td>
            </ng-container>
            <tr mat-header-row *matHeaderRowDef="lineColumns"></tr>
            <tr mat-row *matRowDef="let row; columns: lineColumns"></tr>
          </table>
        </div>
      </mat-card>
    }
  `,
  styles: `.detail { margin-top: 16px; padding: 8px 16px 16px; }`
})
export class JournalPage {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly columns = ['no', 'date', 'source', 'memo', 'total'];
  protected readonly lineColumns = ['account', 'description', 'debit', 'credit'];
  protected readonly rows = signal<JournalEntrySummary[]>([]);
  protected readonly total = signal(0);
  protected readonly selected = signal<JournalEntryDetail | null>(null);
  private readonly now = new Date();
  protected readonly range = inject(FormBuilder).nonNullable.group({
    from: [toIso(new Date(this.now.getFullYear(), this.now.getMonth(), 1))],
    to: [toIso(new Date(this.now.getFullYear(), this.now.getMonth() + 1, 0))]
  });

  constructor() {
    this.load(0);
  }

  load(pageIndex: number): void {
    const { from, to } = this.range.getRawValue();
    this.api.journalEntries({ from, to, page: pageIndex + 1, pageSize: 50 }).subscribe({
      next: (r) => {
        this.rows.set(r.items);
        this.total.set(r.total);
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected page(e: PageEvent): void {
    this.load(e.pageIndex);
  }

  protected open(e: JournalEntrySummary): void {
    this.api.journalEntry(e.id).subscribe((d) => this.selected.set(d));
  }
}
