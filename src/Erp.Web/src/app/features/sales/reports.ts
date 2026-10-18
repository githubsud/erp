import { DecimalPipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormControl } from '@angular/forms';
import { todayIso } from '../../core/dates';
import { NotifyService } from '../../core/notify.service';
import { SalesApi } from '../../core/sales.api';
import { CustomerAgingReport, PdcReport, SalesAnalysisReport, UninvoicedRow } from '../../core/sales.models';
import { SHARED } from '../../shared/shared';

@Component({
  selector: 'app-sales-reports',
  imports: [...SHARED, DecimalPipe],
  template: `
    <div class="page-header"><h1>{{ 'srep.title' | t }}</h1></div>
    <mat-tab-group mat-stretch-tabs="false" (selectedIndexChange)="select($event)">
      <mat-tab [label]="'srep.aging' | t">
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
              <ng-container matColumnDef="customer">
                <th mat-header-cell *matHeaderCellDef>{{ 'cust.customer' | t }}</th>
                <td mat-cell *matCellDef="let r"><a [routerLink]="['/sales/customers', r.customerId]"><span class="ltr">{{ r.code }}</span> · {{ r | bi }}</a></td>
              </ng-container>
              @for (c of agingColumns; track c.key) {
                <ng-container [matColumnDef]="c.key">
                  <th mat-header-cell *matHeaderCellDef class="num">{{ c.label | t }}</th>
                  <td mat-cell *matCellDef="let r" class="num ltr" [class.late]="c.key === 'over90' && r.over90 > 0">{{ r[c.key] ? (r[c.key] | money) : '' }}</td>
                </ng-container>
              }
              <ng-container matColumnDef="limit">
                <th mat-header-cell *matHeaderCellDef class="num">{{ 'cust.creditLimit' | t }}</th>
                <td mat-cell *matCellDef="let r" class="num ltr" [class.late]="r.creditLimit !== null && r.total > r.creditLimit">
                  {{ r.creditLimit === null ? '—' : (r.creditLimit | money) }}</td>
              </ng-container>
              <tr mat-header-row *matHeaderRowDef="agingDisplay"></tr>
              <tr mat-row *matRowDef="let row; columns: agingDisplay"></tr>
            </table>
            @if (a.rows.length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
          </div>
        }
      </mat-tab>

      <mat-tab [label]="'srep.pdc' | t">
        @if (pdc(); as p) {
          <div class="reconcile tab-body" [class.bad]="p.total !== p.ledgerBalance">
            {{ 'po.total' | t }} <b class="ltr">{{ p.total | money }}</b> · {{ 'prep.ledger' | t }} <b class="ltr">{{ p.ledgerBalance | money }}</b>
          </div>
          <div class="table-wrap">
            <table mat-table [dataSource]="p.rows">
              <ng-container matColumnDef="date">
                <th mat-header-cell *matHeaderCellDef>{{ 'rcpt.chequeDate' | t }}</th>
                <td mat-cell *matCellDef="let r" class="ltr">{{ r.chequeDate | d }}</td>
              </ng-container>
              <ng-container matColumnDef="due">
                <th mat-header-cell *matHeaderCellDef>{{ 'srep.dueIn' | t }}</th>
                <td mat-cell *matCellDef="let r">
                  @if (r.daysToDue <= 0) { <span class="chip ok">{{ 'srep.dueNow' | t }}</span> }
                  @else { <span class="ltr">{{ r.daysToDue }}</span> {{ 'app.days' | t }} }
                </td>
              </ng-container>
              <ng-container matColumnDef="customer">
                <th mat-header-cell *matHeaderCellDef>{{ 'cust.customer' | t }}</th>
                <td mat-cell *matCellDef="let r">{{ r | bi:'customerName' }}</td>
              </ng-container>
              <ng-container matColumnDef="cheque">
                <th mat-header-cell *matHeaderCellDef>{{ 'rcpt.chequeNo' | t }}</th>
                <td mat-cell *matCellDef="let r"><span class="ltr">{{ r.chequeNo }}</span> <span class="secondary">{{ r.chequeBank }}</span></td>
              </ng-container>
              <ng-container matColumnDef="amount">
                <th mat-header-cell *matHeaderCellDef class="num">{{ 'app.amount' | t }}</th>
                <td mat-cell *matCellDef="let r" class="num ltr">{{ r.amount | money }}</td>
              </ng-container>
              <tr mat-header-row *matHeaderRowDef="['date', 'due', 'customer', 'cheque', 'amount']"></tr>
              <tr mat-row *matRowDef="let row; columns: ['date', 'due', 'customer', 'cheque', 'amount']"></tr>
            </table>
            @if (p.rows.length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
          </div>
        }
      </mat-tab>

      <mat-tab [label]="'srep.uninvoiced' | t">
        @if (uninvoiced(); as u) {
          <div class="table-wrap tab-body">
            <table mat-table [dataSource]="u">
              <ng-container matColumnDef="dn">
                <th mat-header-cell *matHeaderCellDef>{{ 'dn.no' | t }}</th>
                <td mat-cell *matCellDef="let r"><span class="ltr">{{ r.dnNo }}</span> <span class="secondary ltr">{{ r.deliveryDate | d }}</span></td>
              </ng-container>
              <ng-container matColumnDef="customer">
                <th mat-header-cell *matHeaderCellDef>{{ 'cust.customer' | t }}</th>
                <td mat-cell *matCellDef="let r">{{ r | bi:'customerName' }}</td>
              </ng-container>
              <ng-container matColumnDef="item">
                <th mat-header-cell *matHeaderCellDef>{{ 'po.item' | t }}</th>
                <td mat-cell *matCellDef="let r"><span class="ltr">{{ r.sku }}</span> · {{ r | bi }}</td>
              </ng-container>
              <ng-container matColumnDef="qty">
                <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.qty' | t }}</th>
                <td mat-cell *matCellDef="let r" class="num ltr">{{ r.quantity }}</td>
              </ng-container>
              <ng-container matColumnDef="value">
                <th mat-header-cell *matHeaderCellDef class="num">{{ 'srep.salesValue' | t }}</th>
                <td mat-cell *matCellDef="let r" class="num ltr">{{ r.value | money }}</td>
              </ng-container>
              <tr mat-header-row *matHeaderRowDef="['dn', 'customer', 'item', 'qty', 'value']"></tr>
              <tr mat-row *matRowDef="let row; columns: ['dn', 'customer', 'item', 'qty', 'value']"></tr>
            </table>
            @if (u.length === 0) { <div class="empty">{{ 'srep.allInvoiced' | t }}</div> }
          </div>
        }
      </mat-tab>

      <mat-tab [label]="'srep.analysis' | t">
        <div class="toolbar-row tab-body">
          <mat-form-field><mat-label>{{ 'app.from' | t }}</mat-label><input matInput type="date" [formControl]="from" /></mat-form-field>
          <mat-form-field><mat-label>{{ 'app.to' | t }}</mat-label><input matInput type="date" [formControl]="to" /></mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'srep.groupBy' | t }}</mat-label>
            <mat-select [formControl]="groupBy">
              <mat-option value="item">{{ 'po.item' | t }}</mat-option>
              <mat-option value="customer">{{ 'cust.customer' | t }}</mat-option>
            </mat-select>
          </mat-form-field>
          <button mat-flat-button (click)="loadAnalysis()">{{ 'finance.run' | t }}</button>
        </div>
        @if (analysis(); as a) {
          <div class="card-grid">
            <mat-card appearance="outlined" class="stat"><div class="label">{{ 'srep.netRevenue' | t }}</div><div class="value ltr">{{ a.netRevenue | money }}</div></mat-card>
            <mat-card appearance="outlined" class="stat"><div class="label">{{ 'sinv.cost' | t }}</div><div class="value ltr">{{ a.cost | money }}</div></mat-card>
            <mat-card appearance="outlined" class="stat"><div class="label">{{ 'sinv.margin' | t }}</div><div class="value ltr">{{ a.margin | money }}</div>
              @if (a.netRevenue) { <div class="hint ltr">{{ (a.margin * 100) / a.netRevenue | number: '1.0-1' }}%</div> }</mat-card>
          </div>
          <div class="table-wrap">
            <table mat-table [dataSource]="a.rows">
              <ng-container matColumnDef="name">
                <th mat-header-cell *matHeaderCellDef>{{ (a.groupBy === 'item' ? 'po.item' : 'cust.customer') | t }}</th>
                <td mat-cell *matCellDef="let r"><span class="ltr">{{ r.code }}</span> · {{ r | bi }}</td>
              </ng-container>
              <ng-container matColumnDef="qty">
                <th mat-header-cell *matHeaderCellDef class="num">{{ 'po.qty' | t }}</th>
                <td mat-cell *matCellDef="let r" class="num ltr">{{ a.groupBy === 'item' ? r.quantity : '' }}</td>
              </ng-container>
              <ng-container matColumnDef="revenue">
                <th mat-header-cell *matHeaderCellDef class="num">{{ 'srep.revenue' | t }}</th>
                <td mat-cell *matCellDef="let r" class="num ltr">{{ r.revenue | money }}</td>
              </ng-container>
              <ng-container matColumnDef="returns">
                <th mat-header-cell *matHeaderCellDef class="num">{{ 'srep.returns' | t }}</th>
                <td mat-cell *matCellDef="let r" class="num ltr">{{ r.returns ? (r.returns | money) : '' }}</td>
              </ng-container>
              <ng-container matColumnDef="net">
                <th mat-header-cell *matHeaderCellDef class="num">{{ 'srep.netRevenue' | t }}</th>
                <td mat-cell *matCellDef="let r" class="num ltr">{{ r.netRevenue | money }}</td>
              </ng-container>
              <ng-container matColumnDef="cost">
                <th mat-header-cell *matHeaderCellDef class="num">{{ 'sinv.cost' | t }}</th>
                <td mat-cell *matCellDef="let r" class="num ltr">{{ r.cost | money }}</td>
              </ng-container>
              <ng-container matColumnDef="margin">
                <th mat-header-cell *matHeaderCellDef class="num">{{ 'sinv.margin' | t }}</th>
                <td mat-cell *matCellDef="let r" class="num ltr" [class.late]="r.margin < 0">{{ r.margin | money }}
                  @if (r.marginPercent !== null) { <div class="secondary">{{ r.marginPercent }}%</div> }</td>
              </ng-container>
              <tr mat-header-row *matHeaderRowDef="analysisColumns"></tr>
              <tr mat-row *matRowDef="let row; columns: analysisColumns"></tr>
            </table>
            @if (a.rows.length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
          </div>
        }
      </mat-tab>
    </mat-tab-group>
  `,
  styles: `
    .tab-body { margin-block-start: 16px; }
    .reconcile { padding: 12px 16px; border-radius: 12px; background: #d8f3e5; color: #0b5a35; margin-block-end: 12px; }
    .reconcile.bad { background: #ffdcd9; color: #8c1d18; }
    .late { color: #b3261e; font-weight: 600; }
  `
})
export class SalesReportsPage {
  private readonly sales = inject(SalesApi);
  private readonly notify = inject(NotifyService);

  protected readonly agingColumns = [
    { key: 'current', label: 'prep.current' },
    { key: 'days1To30', label: 'prep.d30' },
    { key: 'days31To60', label: 'prep.d60' },
    { key: 'days61To90', label: 'prep.d90' },
    { key: 'over90', label: 'prep.over90' },
    { key: 'total', label: 'po.total' }
  ] as const;
  protected readonly agingDisplay = ['customer', ...this.agingColumns.map((c) => c.key), 'limit'];
  protected readonly analysisColumns = ['name', 'qty', 'revenue', 'returns', 'net', 'cost', 'margin'];
  protected readonly asOf = new FormControl(todayIso(), { nonNullable: true });
  protected readonly from = new FormControl(`${todayIso().slice(0, 7)}-01`, { nonNullable: true });
  protected readonly to = new FormControl(todayIso(), { nonNullable: true });
  protected readonly groupBy = new FormControl<'item' | 'customer'>('item', { nonNullable: true });
  protected readonly aging = signal<CustomerAgingReport | null>(null);
  protected readonly pdc = signal<PdcReport | null>(null);
  protected readonly uninvoiced = signal<UninvoicedRow[] | null>(null);
  protected readonly analysis = signal<SalesAnalysisReport | null>(null);

  constructor() {
    this.loadAging();
  }

  protected select(index: number): void {
    if (index === 1 && !this.pdc()) this.sales.pdc().subscribe({ next: (p) => this.pdc.set(p), error: (e) => this.notify.error(e) });
    if (index === 2 && !this.uninvoiced()) this.sales.uninvoiced().subscribe({ next: (u) => this.uninvoiced.set(u), error: (e) => this.notify.error(e) });
    if (index === 3 && !this.analysis()) this.loadAnalysis();
  }

  protected loadAging(): void {
    this.sales.aging(this.asOf.value).subscribe({ next: (a) => this.aging.set(a), error: (e) => this.notify.error(e) });
  }

  protected loadAnalysis(): void {
    this.sales.analysis(this.from.value, this.to.value, this.groupBy.value).subscribe({ next: (a) => this.analysis.set(a), error: (e) => this.notify.error(e) });
  }
}
