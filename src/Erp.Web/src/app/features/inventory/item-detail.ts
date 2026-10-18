import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { FormControl } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { AuthService } from '../../core/auth.service';
import { todayIso } from '../../core/dates';
import { NotifyService } from '../../core/notify.service';
import { TradeApi } from '../../core/trade.api';
import { Item, ItemLedger, Master } from '../../core/trade.models';
import { SHARED } from '../../shared/shared';
import { ItemDialog } from './items';

/** Translation keys for the documents that move stock. */
export const SOURCE_LABELS: Record<string, string> = {
  stock_adjustment: 'nav.adjustments',
  stock_transfer: 'nav.transfers',
  goods_receipt: 'po.receipts',
  supplier_invoice: 'nav.invoices',
  landed_cost: 'nav.landedCosts'
};

@Component({
  selector: 'app-item-detail',
  imports: [...SHARED],
  template: `
    @if (item(); as i) {
      <div class="page-header">
        <a mat-icon-button routerLink="/inventory/items"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
        <h1><span class="ltr">{{ i.sku }}</span> · {{ i | bi }}</h1>
        @if (auth.can('inv.items:manage')) {
          <button mat-stroked-button (click)="edit(i)"><mat-icon>edit</mat-icon> {{ 'app.edit' | t }}</button>
        }
      </div>
      <div class="card-grid">
        <mat-card appearance="outlined" class="stat">
          <div class="label">{{ 'item.onHand' | t }}</div>
          <div class="value ltr">{{ i.quantityOnHand }} <span class="secondary">{{ i.uomCode }}</span></div>
          <div class="hint">{{ 'item.type.' + i.itemType | t }} · {{ groupName(i.itemGroupId) }}</div>
        </mat-card>
        <mat-card appearance="outlined" class="stat">
          <div class="label">{{ 'item.avgCost' | t }}</div>
          <div class="value ltr">{{ i.averageCost | money }}</div>
          @if (i.purchasePrice) { <div class="hint">{{ 'item.purchasePrice' | t }}: <span class="ltr">{{ i.purchasePrice | money }}</span></div> }
        </mat-card>
        <mat-card appearance="outlined" class="stat">
          <div class="label">{{ 'item.value' | t }}</div>
          <div class="value ltr">{{ i.stockValue | money }}</div>
          @if (i.reorderLevel !== null) { <div class="hint">{{ 'item.reorderLevel' | t }}: <span class="ltr">{{ i.reorderLevel }}</span></div> }
        </mat-card>
      </div>

      @if (i.itemType === 'Stock') {
        <div class="toolbar-row">
          <div class="section-title">{{ 'item.ledger' | t }}</div>
          <span class="spacer"></span>
          <mat-form-field>
            <mat-label>{{ 'app.from' | t }}</mat-label>
            <input matInput type="date" [formControl]="from" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'app.to' | t }}</mat-label>
            <input matInput type="date" [formControl]="to" />
          </mat-form-field>
          <button mat-stroked-button (click)="loadLedger()">{{ 'finance.run' | t }}</button>
        </div>
        @if (ledger(); as l) {
          <div class="table-wrap">
            <table class="ledger">
              <thead>
                <tr>
                  <th>{{ 'app.date' | t }}</th><th>{{ 'item.document' | t }}</th><th>{{ 'stock.warehouse' | t }}</th>
                  <th class="num">{{ 'item.in' | t }}</th><th class="num">{{ 'item.out' | t }}</th><th class="num">{{ 'adj.unitCost' | t }}</th>
                  <th class="num">{{ 'grn.value' | t }}</th><th class="num">{{ 'item.balance' | t }}</th><th class="num">{{ 'item.value' | t }}</th>
                </tr>
              </thead>
              <tbody>
                <tr class="opening">
                  <td colspan="7">{{ 'item.opening' | t }}</td>
                  <td class="num ltr">{{ l.openingQuantity }}</td><td class="num ltr">{{ l.openingValue | money }}</td>
                </tr>
                @for (r of l.rows; track r.id) {
                  <tr>
                    <td class="ltr">{{ r.date | d }}</td>
                    <td>{{ sourceLabel(r.sourceType) | t }} <span class="ltr">{{ r.sourceNo }}</span> @if (r.isReversal) { <span class="chip warn">{{ 'item.reversal' | t }}</span> }</td>
                    <td>{{ warehouseName(r.warehouseId) }}</td>
                    <td class="num ltr">{{ r.quantity > 0 ? r.quantity : '' }}</td>
                    <td class="num ltr">{{ r.quantity < 0 ? -r.quantity : '' }}</td>
                    <td class="num ltr">{{ r.quantity !== 0 ? (r.unitCost | money) : '' }}</td>
                    <td class="num ltr">{{ r.value | money }}</td>
                    <td class="num ltr">{{ r.quantityAfter }}</td>
                    <td class="num ltr">{{ r.valueAfter | money }}</td>
                  </tr>
                }
              </tbody>
            </table>
            @if (l.rows.length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
          </div>
        }
      }
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: `
    .spacer { flex: 1; }
    .ledger { border-collapse: collapse; }
    .ledger th, .ledger td { padding: 10px 12px; border-bottom: 1px solid var(--mat-sys-outline-variant); text-align: start; }
    .ledger th { font: var(--mat-sys-label-large); color: var(--mat-sys-on-surface-variant); }
    .ledger .num { text-align: end; }
    .opening td { font-weight: 600; background: var(--mat-sys-surface-container-low); }
  `
})
export class ItemDetailPage implements OnInit {
  readonly id = input.required<string>();
  protected readonly auth = inject(AuthService);
  private readonly trade = inject(TradeApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);

  protected readonly item = signal<Item | null>(null);
  protected readonly ledger = signal<ItemLedger | null>(null);
  private readonly groups = signal<Master[]>([]);
  private readonly warehouses = signal<Master[]>([]);
  private readonly warehouseMap = computed(() => new Map(this.warehouses().map((w) => [w.id, w])));
  protected readonly from = new FormControl(`${new Date().getFullYear()}-01-01`, { nonNullable: true });
  protected readonly to = new FormControl(todayIso(), { nonNullable: true });

  ngOnInit(): void {
    this.trade.groups().subscribe((g) => this.groups.set(g));
    this.trade.warehouses().subscribe((w) => this.warehouses.set(w));
    this.load();
  }

  protected sourceLabel(type: string): string {
    return SOURCE_LABELS[type] ?? type;
  }

  protected groupName(id: string): string {
    const g = this.groups().find((x) => x.id === id);
    return g ? g.code : '';
  }

  protected warehouseName(id: string | null): string {
    return id ? (this.warehouseMap().get(id)?.code ?? '') : '—';
  }

  protected edit(item: Item): void {
    this.dialog
      .open(ItemDialog, { data: { item } })
      .afterClosed()
      .subscribe((saved?: Item) => saved && this.item.set(saved));
  }

  protected loadLedger(): void {
    this.trade.itemLedger(this.id(), this.from.value, this.to.value).subscribe({ next: (l) => this.ledger.set(l), error: (e) => this.notify.error(e) });
  }

  private load(): void {
    this.trade.item(this.id()).subscribe({
      next: (i) => {
        this.item.set(i);
        if (i.itemType === 'Stock') this.loadLedger();
      },
      error: (e) => this.notify.error(e)
    });
  }
}
