import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl } from '@angular/forms';
import { merge } from 'rxjs';
import { AuthService } from '../../core/auth.service';
import { todayIso } from '../../core/dates';
import { NotifyService } from '../../core/notify.service';
import { TradeApi } from '../../core/trade.api';
import { Master, StockRow, ValuationReport } from '../../core/trade.models';
import { inBoxes } from '../../core/item-units';
import { SHARED } from '../../shared/shared';

@Component({
  selector: 'app-stock',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'stock.title' | t }}</h1></div>
    <mat-tab-group mat-stretch-tabs="false" (selectedIndexChange)="$event === 1 && loadValuation()">
      <mat-tab [label]="'stock.title' | t">
        <div class="toolbar-row tab-body">
          <mat-form-field>
            <mat-label>{{ 'stock.warehouse' | t }}</mat-label>
            <mat-select [formControl]="warehouse">
              <mat-option value="">{{ 'stock.allWarehouses' | t }}</mat-option>
              @for (w of warehouses(); track w.id) { <mat-option [value]="w.id">{{ w.code }} · {{ w | bi }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <mat-slide-toggle [formControl]="belowReorder">{{ 'stock.belowReorder' | t }}</mat-slide-toggle>
        </div>
        <div class="table-wrap">
          <table mat-table [dataSource]="rows()">
            <ng-container matColumnDef="item">
              <th mat-header-cell *matHeaderCellDef>{{ 'item.name' | t }}</th>
              <td mat-cell *matCellDef="let r"><a [routerLink]="['/inventory/items', r.itemId]"><span class="ltr">{{ r.sku }}</span> · {{ r | bi }}</a></td>
              <td mat-footer-cell *matFooterCellDef>{{ 'app.total' | t }}</td>
            </ng-container>
            <ng-container matColumnDef="qty">
              <th mat-header-cell *matHeaderCellDef class="num">{{ 'stock.quantity' | t }}</th>
              <td mat-cell *matCellDef="let r" class="num ltr">{{ r.quantity }} <span class="secondary">{{ r.uomCode }}</span>
                @if (r.boxes?.length && r.quantity > 0) { <div class="secondary">= {{ boxes(r) }}</div> }</td>
              <td mat-footer-cell *matFooterCellDef></td>
            </ng-container>
            <ng-container matColumnDef="avg">
              <th mat-header-cell *matHeaderCellDef class="num">{{ 'item.avgCost' | t }}</th>
              <td mat-cell *matCellDef="let r" class="num ltr">{{ r.averageCost | money }}</td>
              <td mat-footer-cell *matFooterCellDef></td>
            </ng-container>
            <ng-container matColumnDef="value">
              <th mat-header-cell *matHeaderCellDef class="num">{{ 'item.value' | t }}</th>
              <td mat-cell *matCellDef="let r" class="num ltr">{{ r.value | money }}</td>
              <td mat-footer-cell *matFooterCellDef class="num ltr">{{ totalValue() | money }}</td>
            </ng-container>
            <ng-container matColumnDef="reorder">
              <th mat-header-cell *matHeaderCellDef>{{ 'item.reorderLevel' | t }}</th>
              <td mat-cell *matCellDef="let r">
                @if (r.reorderLevel !== null) { <span class="ltr">{{ r.reorderLevel }}</span> }
                @if (r.belowReorder) { <span class="chip warn">{{ 'stock.reorder' | t }}</span> }
              </td>
              <td mat-footer-cell *matFooterCellDef></td>
            </ng-container>
            <tr mat-header-row *matHeaderRowDef="columns"></tr>
            <tr mat-row *matRowDef="let row; columns: columns"></tr>
            <tr mat-footer-row *matFooterRowDef="columns"></tr>
          </table>
          @if (rows().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
        </div>
      </mat-tab>

      @if (auth.can('inv.reports:read')) {
        <mat-tab [label]="'stock.valuation' | t">
          <div class="toolbar-row tab-body">
            <mat-form-field>
              <mat-label>{{ 'stock.asOf' | t }}</mat-label>
              <input matInput type="date" [formControl]="asOf" />
            </mat-form-field>
            <button mat-flat-button (click)="loadValuation()">{{ 'finance.run' | t }}</button>
          </div>
          @if (valuation(); as v) {
            <div class="card-grid">
              <mat-card appearance="outlined" class="stat">
                <div class="label">{{ 'stock.valuation' | t }}</div>
                <div class="value ltr">{{ v.totalValue | money }}</div>
              </mat-card>
              <mat-card appearance="outlined" class="stat">
                <div class="label">{{ 'stock.ledgerBalance' | t }}</div>
                <div class="value ltr">{{ v.ledgerBalance | money }}</div>
              </mat-card>
              <mat-card appearance="outlined" class="stat">
                <div class="label">{{ 'stock.difference' | t }}</div>
                <div class="value ltr" [class.bad]="v.difference !== 0">{{ v.difference | money }}</div>
                <div class="hint">{{ (v.difference === 0 ? 'stock.reconciled' : 'stock.notReconciled') | t }}</div>
              </mat-card>
            </div>
            <div class="table-wrap">
              <table mat-table [dataSource]="v.rows">
                <ng-container matColumnDef="item">
                  <th mat-header-cell *matHeaderCellDef>{{ 'item.name' | t }}</th>
                  <td mat-cell *matCellDef="let r"><span class="ltr">{{ r.sku }}</span> · {{ r | bi }}</td>
                </ng-container>
                <ng-container matColumnDef="qty">
                  <th mat-header-cell *matHeaderCellDef class="num">{{ 'stock.quantity' | t }}</th>
                  <td mat-cell *matCellDef="let r" class="num ltr">{{ r.quantity }}</td>
                </ng-container>
                <ng-container matColumnDef="value">
                  <th mat-header-cell *matHeaderCellDef class="num">{{ 'item.value' | t }}</th>
                  <td mat-cell *matCellDef="let r" class="num ltr">{{ r.value | money }}</td>
                </ng-container>
                <tr mat-header-row *matHeaderRowDef="['item', 'qty', 'value']"></tr>
                <tr mat-row *matRowDef="let row; columns: ['item', 'qty', 'value']"></tr>
              </table>
            </div>
          }
        </mat-tab>
      }
    </mat-tab-group>
  `,
  styles: `
    .tab-body { margin-block-start: 16px; }
    .bad { color: #8c1d18; }
    a { color: inherit; }
  `
})
export class StockPage {
  protected boxes(r: StockRow): string {
    return inBoxes(r.quantity, r.uomCode, r.boxes ?? []);
  }

  protected readonly auth = inject(AuthService);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);

  protected readonly columns = ['item', 'qty', 'avg', 'value', 'reorder'];
  protected readonly warehouse = new FormControl('', { nonNullable: true });
  protected readonly belowReorder = new FormControl(false, { nonNullable: true });
  protected readonly asOf = new FormControl(todayIso(), { nonNullable: true });
  protected readonly warehouses = signal<Master[]>([]);
  protected readonly rows = signal<StockRow[]>([]);
  protected readonly valuation = signal<ValuationReport | null>(null);
  protected readonly totalValue = computed(() => this.rows().reduce((s, r) => s + r.value, 0));

  constructor() {
    this.trade.warehouses().subscribe((w) => this.warehouses.set(w.filter((x) => x.isActive)));
    merge(this.warehouse.valueChanges, this.belowReorder.valueChanges).pipe(takeUntilDestroyed()).subscribe(() => this.load());
    this.load();
  }

  protected loadValuation(): void {
    this.trade.valuation(this.asOf.value).subscribe({ next: (v) => this.valuation.set(v), error: (e) => this.notify.error(e) });
  }

  private load(): void {
    this.trade.stock(this.warehouse.value || null, this.belowReorder.value).subscribe({ next: (r) => this.rows.set(r), error: (e) => this.notify.error(e) });
  }
}
