import { ItemBatches } from './batches';
import { ItemBins, ItemSerials } from './serials';
import { ItemKit } from './kits';
import { ItemPartsCard } from './parts';
import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { FormControl, Validators } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { AuthService } from '../../core/auth.service';
import { todayIso } from '../../core/dates';
import { NotifyService } from '../../core/notify.service';
import { TradeApi, UnitRequest } from '../../core/trade.api';
import { Item, ItemLedger, ItemUnit, Master } from '../../core/trade.models';
import { SHARED } from '../../shared/shared';
import { ItemDialog } from './items';

/** Translation keys for the documents that move stock. */
export const SOURCE_LABELS: Record<string, string> = {
  stock_adjustment: 'nav.adjustments',
  stock_transfer: 'nav.transfers',
  goods_receipt: 'po.receipts',
  supplier_invoice: 'nav.invoices',
  landed_cost: 'nav.landedCosts',
  delivery_note: 'dn.title',
  sales_invoice: 'nav.salesInvoices',
  credit_note: 'nav.creditNotes',
  debit_note: 'pdn.title'
};

@Component({
  selector: 'app-item-detail',
  imports: [...SHARED, ItemBatches, ItemBins, ItemSerials, ItemKit, ItemPartsCard],
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
          <div class="hint">{{ 'item.type.' + i.itemType | t }} · {{ groupName(i.itemGroupId) }}@if (i.brand) { · <span class="ltr">{{ i.brand }}</span> }</div>
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

      @if (i.itemType === 'Stock' && !i.trackBatches) {
        <mat-card appearance="outlined" class="form-card units">
          <div class="section-title">{{ 'kit.title' | t }}</div>
          <app-item-kit [itemId]="i.id" />
        </mat-card>
      }
      @if (i.itemType === 'Stock') {
        <mat-card appearance="outlined" class="form-card parts">
          <div class="section-title">{{ 'parts.title' | t }}</div>
          <app-item-parts [itemId]="i.id" />
        </mat-card>
      }
      <mat-card appearance="outlined" class="form-card units">
        <div class="section-title">{{ 'bin.title' | t }}</div>
        <app-item-bins [itemId]="i.id" />
      </mat-card>
      @if (i.trackSerials) {
        <mat-card appearance="outlined" class="form-card units">
          <div class="section-title">{{ 'serial.onHand' | t }}</div>
          <app-item-serials [itemId]="i.id" />
        </mat-card>
      } @else if (i.trackBatches) {
        <mat-card appearance="outlined" class="form-card units">
          <div class="section-title">{{ 'batch.title' | t }}</div>
          <app-item-batches [itemId]="i.id" />
        </mat-card>
      }

      <mat-card appearance="outlined" class="form-card units">
        <div class="section-title">{{ 'units.title' | t }}</div>
        <p class="secondary">{{ 'units.hint' | t: { base: i.uomCode } }}</p>
        <table class="line-editor">
          <tbody>
            @for (u of alternatives(); track u.uomId) {
              <tr>
                <td>
                  1 <b class="ltr">{{ u.uomCode }}</b> = <b class="ltr">{{ u.factor }}</b> {{ i.uomCode }}
                  · <span class="ltr">{{ unitPrice(u, i) | money }}</span>
                  @if (u.salesPrice != null) { <span class="chip info">{{ 'units.ownPrice' | t }}</span> }
                  @if (u.barcode) { · <mat-icon class="inline">qr_code_2</mat-icon> <span class="ltr">{{ u.barcode }}</span> }
                </td>
                <td>@if (canManage) {
                  <button mat-icon-button (click)="editUnit(u)" [attr.aria-label]="'units.edit' | t"><mat-icon>edit</mat-icon></button>
                  <button mat-icon-button (click)="removeUnit(u)"><mat-icon>delete</mat-icon></button>
                }</td>
              </tr>
            }
            @if (canManage) {
              <tr>
                <td class="new-unit">
                  <mat-form-field><mat-label>{{ 'units.unit' | t }}</mat-label>
                    <mat-select [formControl]="newUom">
                      @for (u of uomChoices(); track u.id) { <mat-option [value]="u.id">{{ u.code }} · {{ u | bi }}</mat-option> }
                    </mat-select>
                  </mat-form-field>
                  <mat-form-field><mat-label>{{ 'units.factor' | t: { base: i.uomCode } }}</mat-label>
                    <input matInput type="number" min="0" [formControl]="newFactor" dir="ltr" />
                  </mat-form-field>
                  <mat-form-field><mat-label>{{ 'units.price' | t }}</mat-label>
                    <input matInput type="number" min="0" [formControl]="newPrice" dir="ltr" />
                    @if (i.salesPrice != null && Number(newFactor.value) > 0) {
                      <mat-hint>{{ 'units.priceHint' | t: { price: (i.salesPrice * Number(newFactor.value)).toFixed(2), n: Number(newFactor.value) } }}</mat-hint>
                    }
                  </mat-form-field>
                  <mat-form-field><mat-label>{{ 'units.barcode' | t }}</mat-label>
                    <input matInput [formControl]="newBarcode" dir="ltr" autocomplete="off" />
                  </mat-form-field>
                </td>
                <td><button mat-stroked-button (click)="addUnit()" [disabled]="!newUom.value || !(Number(newFactor.value) > 0)">
                  <mat-icon>{{ editing() ? 'save' : 'add' }}</mat-icon> {{ (editing() ? 'units.save' : 'tset.add') | t }}</button></td>
              </tr>
            }
          </tbody>
        </table>
      </mat-card>

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
    .units { max-width: 720px; }
    .parts { max-width: 960px; }
    mat-icon.inline { font-size: 16px; width: 16px; height: 16px; vertical-align: middle; }
    .new-unit { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
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
  protected readonly canManage = this.auth.can('inv.items:manage');
  protected readonly Number = Number;
  protected readonly units = signal<ItemUnit[]>([]);
  protected readonly alternatives = computed(() => this.units().filter((u) => !u.isBase));
  private readonly uoms = signal<Master[]>([]);
  /** The unit being changed (it stays in the choices while its form is open). */
  protected readonly editing = signal<string | null>(null);
  protected readonly uomChoices = computed(() => this.uoms().filter((u) => u.id === this.editing() || !this.units().some((x) => x.uomId === u.id)));
  protected readonly newUom = new FormControl<string | null>(null);
  protected readonly newFactor = new FormControl<number | null>(null, Validators.min(0));
  protected readonly newPrice = new FormControl<number | null>(null, Validators.min(0));
  protected readonly newBarcode = new FormControl<string>('', { nonNullable: true });
  protected readonly from = new FormControl(`${new Date().getFullYear()}-01-01`, { nonNullable: true });
  protected readonly to = new FormControl(todayIso(), { nonNullable: true });

  ngOnInit(): void {
    this.trade.groups().subscribe((g) => this.groups.set(g));
    this.trade.warehouses().subscribe((w) => this.warehouses.set(w));
    this.trade.uoms().subscribe((u) => this.uoms.set(u));
    this.trade.units(this.id()).subscribe((u) => this.units.set(u));
    this.load();
  }

  /** One of this unit at the item list's price: its own price, else the item's price times its size. */
  protected unitPrice(u: ItemUnit, item: Item): number | null {
    return u.salesPrice ?? (item.salesPrice == null ? null : Math.round(item.salesPrice * u.factor * 100) / 100);
  }

  private static request(u: ItemUnit): UnitRequest {
    return { uomId: u.uomId, factor: u.factor, salesPrice: u.salesPrice ?? null, barcode: u.barcode ?? null };
  }

  protected editUnit(u: ItemUnit): void {
    this.editing.set(u.uomId);
    this.newUom.setValue(u.uomId);
    this.newFactor.setValue(u.factor);
    this.newPrice.setValue(u.salesPrice ?? null);
    this.newBarcode.setValue(u.barcode ?? '');
  }

  protected addUnit(): void {
    const price = this.newPrice.value;
    this.saveUnits([
      ...this.alternatives().filter((u) => u.uomId !== this.newUom.value).map(ItemDetailPage.request),
      {
        uomId: this.newUom.value!, factor: Number(this.newFactor.value), salesPrice: price == null || String(price) === '' ? null : Number(price),
        barcode: this.newBarcode.value.trim() || null
      }
    ]);
  }

  protected removeUnit(unit: ItemUnit): void {
    this.saveUnits(this.alternatives().filter((u) => u.uomId !== unit.uomId).map(ItemDetailPage.request));
  }

  private saveUnits(list: UnitRequest[]): void {
    this.trade.saveUnits(this.id(), list).subscribe({
      next: (u) => {
        this.units.set(u);
        this.editing.set(null);
        this.newUom.setValue(null);
        this.newFactor.setValue(null);
        this.newPrice.setValue(null);
        this.newBarcode.setValue('');
        this.notify.success();
      },
      error: (e) => this.notify.error(e)
    });
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
