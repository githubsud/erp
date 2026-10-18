import { Component, inject, signal } from '@angular/core';
import { FormControl } from '@angular/forms';
import { todayIso } from '../../core/dates';
import { NotifyService } from '../../core/notify.service';
import { TradeApi } from '../../core/trade.api';
import { AgingReport, GrniReport, WhtReport } from '../../core/trade.models';
import { SHARED } from '../../shared/shared';
import { currentMonth } from '../payroll/payroll-list';

@Component({
  selector: 'app-purchasing-reports',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'prep.title' | t }}</h1></div>
    <mat-tab-group mat-stretch-tabs="false" (selectedIndexChange)="select($event)">
      <mat-tab [label]="'prep.aging' | t">
        <div class="toolbar-row tab-body">
          <mat-form-field>
            <mat-label>{{ 'stock.asOf' | t }}</mat-label>
            <input matInput type="date" [formControl]="asOf" />
          </mat-form-field>
          <button mat-flat-button (click)="loadAging()">{{ 'finance.run' | t }}</button>
        </div>
        @if (aging(); as a) {
          <div class="reconcile" [class.bad]="a.total !== a.ledgerBalance">
            {{ 'po.total' | t }} <b class="ltr">{{ a.total | money }}</b> · {{ 'prep.ledger' | t }} <b class="ltr">{{ a.ledgerBalance | money }}</b>
          </div>
          <div class="table-wrap">
            <table mat-table [dataSource]="a.rows">
              <ng-container matColumnDef="supplier">
                <th mat-header-cell *matHeaderCellDef>{{ 'po.supplier' | t }}</th>
                <td mat-cell *matCellDef="let r"><span class="ltr">{{ r.code }}</span> · {{ r | bi }}</td>
              </ng-container>
              @for (c of agingColumns; track c.key) {
                <ng-container [matColumnDef]="c.key">
                  <th mat-header-cell *matHeaderCellDef class="num">{{ c.label | t }}</th>
                  <td mat-cell *matCellDef="let r" class="num ltr">{{ r[c.key] ? (r[c.key] | money) : '' }}</td>
                </ng-container>
              }
              <tr mat-header-row *matHeaderRowDef="agingDisplay"></tr>
              <tr mat-row *matRowDef="let row; columns: agingDisplay"></tr>
            </table>
            @if (a.rows.length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
          </div>
        }
      </mat-tab>

      <mat-tab [label]="'prep.grni' | t">
        @if (grni(); as g) {
          <div class="reconcile tab-body" [class.bad]="g.difference !== 0">
            {{ 'po.total' | t }} <b class="ltr">{{ g.total | money }}</b> · {{ 'prep.ledger' | t }} <b class="ltr">{{ g.ledgerBalance | money }}</b>
            · {{ 'stock.difference' | t }} <b class="ltr">{{ g.difference | money }}</b>
          </div>
          <div class="table-wrap">
            <table mat-table [dataSource]="g.rows">
              <ng-container matColumnDef="grn">
                <th mat-header-cell *matHeaderCellDef>{{ 'grn.no' | t }}</th>
                <td mat-cell *matCellDef="let r"><span class="ltr">{{ r.grnNo }}</span> <span class="secondary ltr">{{ r.receiptDate | d }}</span></td>
              </ng-container>
              <ng-container matColumnDef="supplier">
                <th mat-header-cell *matHeaderCellDef>{{ 'po.supplier' | t }}</th>
                <td mat-cell *matCellDef="let r">{{ r | bi:'supplierName' }}</td>
              </ng-container>
              <ng-container matColumnDef="item">
                <th mat-header-cell *matHeaderCellDef>{{ 'po.item' | t }}</th>
                <td mat-cell *matCellDef="let r" class="ltr">{{ r.sku }}</td>
              </ng-container>
              <ng-container matColumnDef="qty">
                <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.qty' | t }}</th>
                <td mat-cell *matCellDef="let r" class="num ltr">{{ r.quantityNotInvoiced }}</td>
              </ng-container>
              <ng-container matColumnDef="value">
                <th mat-header-cell *matHeaderCellDef class="num">{{ 'grn.value' | t }}</th>
                <td mat-cell *matCellDef="let r" class="num ltr">{{ r.value | money }}</td>
              </ng-container>
              <tr mat-header-row *matHeaderRowDef="['grn', 'supplier', 'item', 'qty', 'value']"></tr>
              <tr mat-row *matRowDef="let row; columns: ['grn', 'supplier', 'item', 'qty', 'value']"></tr>
            </table>
            @if (g.rows.length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
          </div>
        }
      </mat-tab>

      <mat-tab [label]="'prep.wht' | t">
        <div class="toolbar-row tab-body">
          <mat-form-field>
            <mat-label>{{ 'payroll.month' | t }}</mat-label>
            <input matInput type="month" [formControl]="month" />
          </mat-form-field>
          <button mat-flat-button (click)="loadWht()">{{ 'finance.run' | t }}</button>
        </div>
        @if (wht(); as w) {
          <div class="reconcile">
            {{ 'prep.deadline' | t: { d: (w.filingDeadline | d) } }} · {{ 'pay.gross' | t }} <b class="ltr">{{ w.totalGross | money }}</b>
            · {{ 'prep.tax' | t }} <b class="ltr">{{ w.totalTax | money }}</b>
          </div>
          <div class="table-wrap">
            <table mat-table [dataSource]="w.rows">
              <ng-container matColumnDef="date">
                <th mat-header-cell *matHeaderCellDef>{{ 'app.date' | t }}</th>
                <td mat-cell *matCellDef="let r" class="ltr">{{ r.paymentDate | d }}</td>
              </ng-container>
              <ng-container matColumnDef="supplier">
                <th mat-header-cell *matHeaderCellDef>{{ 'po.supplier' | t }}</th>
                <td mat-cell *matCellDef="let r">{{ r | bi:'supplierName' }} <span class="secondary ltr">{{ r.country }} {{ r.taxCardNo }}</span></td>
              </ng-container>
              <ng-container matColumnDef="invoice">
                <th mat-header-cell *matHeaderCellDef>{{ 'pinv.no' | t }}</th>
                <td mat-cell *matCellDef="let r"><span class="ltr">{{ r.invoiceNo }}</span> <span class="secondary">{{ r.categories }}</span></td>
              </ng-container>
              <ng-container matColumnDef="gross">
                <th mat-header-cell *matHeaderCellDef class="num">{{ 'pay.gross' | t }}</th>
                <td mat-cell *matCellDef="let r" class="num ltr">{{ r.gross | money }}</td>
              </ng-container>
              <ng-container matColumnDef="rate">
                <th mat-header-cell *matHeaderCellDef class="num">{{ 'prep.rate' | t }}</th>
                <td mat-cell *matCellDef="let r" class="num ltr">{{ r.rate }}%</td>
              </ng-container>
              <ng-container matColumnDef="tax">
                <th mat-header-cell *matHeaderCellDef class="num">{{ 'prep.tax' | t }}</th>
                <td mat-cell *matCellDef="let r" class="num ltr">{{ r.tax | money }}</td>
              </ng-container>
              <tr mat-header-row *matHeaderRowDef="whtColumns"></tr>
              <tr mat-row *matRowDef="let row; columns: whtColumns"></tr>
            </table>
            @if (w.rows.length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
          </div>
        }
      </mat-tab>
    </mat-tab-group>
  `,
  styles: `
    .tab-body { margin-block-start: 16px; }
    .reconcile { padding: 12px 16px; border-radius: 12px; background: #d8f3e5; color: #0b5a35; margin-block-end: 12px; }
    .reconcile.bad { background: #ffdcd9; color: #8c1d18; }
  `
})
export class PurchasingReportsPage {
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);

  protected readonly agingColumns = [
    { key: 'current', label: 'prep.current' },
    { key: 'days1To30', label: 'prep.d30' },
    { key: 'days31To60', label: 'prep.d60' },
    { key: 'days61To90', label: 'prep.d90' },
    { key: 'over90', label: 'prep.over90' },
    { key: 'total', label: 'po.total' }
  ] as const;
  protected readonly agingDisplay = ['supplier', ...this.agingColumns.map((c) => c.key)];
  protected readonly whtColumns = ['date', 'supplier', 'invoice', 'gross', 'rate', 'tax'];
  protected readonly asOf = new FormControl(todayIso(), { nonNullable: true });
  protected readonly month = new FormControl(currentMonth(), { nonNullable: true });
  protected readonly aging = signal<AgingReport | null>(null);
  protected readonly grni = signal<GrniReport | null>(null);
  protected readonly wht = signal<WhtReport | null>(null);

  constructor() {
    this.loadAging();
  }

  protected select(index: number): void {
    if (index === 1 && !this.grni()) this.trade.grni().subscribe({ next: (g) => this.grni.set(g), error: (e) => this.notify.error(e) });
    if (index === 2 && !this.wht()) this.loadWht();
  }

  protected loadAging(): void {
    this.trade.aging(this.asOf.value).subscribe({ next: (a) => this.aging.set(a), error: (e) => this.notify.error(e) });
  }

  protected loadWht(): void {
    const [year, month] = this.month.value.split('-').map(Number);
    this.trade.wht(year, month).subscribe({ next: (w) => this.wht.set(w), error: (e) => this.notify.error(e) });
  }
}
