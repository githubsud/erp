import { Component, effect, inject, input, signal } from '@angular/core';
import { FormControl } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { NotifyService } from '../../core/notify.service';
import { TradeApi } from '../../core/trade.api';
import { Master } from '../../core/trade.models';
import { SHARED } from '../../shared/shared';

interface SerialInStock {
  serialNo: string;
  warehouseId: string;
  warehouseCode: string;
}

interface SerialInfo {
  serialNo: string;
  itemId: string;
  sku: string;
  nameEn: string;
  nameAr: string;
  inStock: boolean;
  warehouseCode: string | null;
  history: { date: string; sourceType: string; sourceNo: string; warehouseCode: string; quantity: number }[];
}

interface Bin {
  warehouseId: string;
  warehouseCode: string;
  binCode: string;
}

/** Serial numbers of one item on hand, by warehouse. */
@Component({
  selector: 'app-item-serials',
  imports: [...SHARED],
  template: `
    <div class="serials">
      @for (s of rows(); track s.serialNo) { <span class="chip ltr" [matTooltip]="s.warehouseCode">{{ s.serialNo }}</span> }
    </div>
    @if (!rows().length) { <div class="empty">{{ 'app.none' | t }}</div> }
  `,
  styles: `.serials { display: flex; flex-wrap: wrap; gap: 6px; }`
})
export class ItemSerials {
  readonly itemId = input.required<string>();
  private readonly api = inject(ApiService);
  protected readonly rows = signal<SerialInStock[]>([]);

  constructor() {
    effect(() => this.api.get<SerialInStock[]>(`inventory/items/${this.itemId()}/serials`).subscribe((r) => this.rows.set(r)));
  }
}

/** Where the item is kept in each warehouse. */
@Component({
  selector: 'app-item-bins',
  imports: [...SHARED],
  template: `
    <table class="line-editor">
      <tbody>
        @for (w of warehouses(); track w.id) {
          <tr>
            <td>{{ w | bi }}</td>
            <td class="narrow"><mat-form-field><input matInput dir="ltr" [formControl]="codes[w.id]" [readonly]="!canManage" placeholder="A-01-3" /></mat-form-field></td>
          </tr>
        }
      </tbody>
    </table>
    @if (canManage) { <button mat-stroked-button (click)="save()">{{ 'app.save' | t }}</button> }
  `
})
export class ItemBins {
  readonly itemId = input.required<string>();
  private readonly api = inject(ApiService);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  protected readonly canManage = inject(AuthService).can('inv.items:manage');
  protected readonly warehouses = signal<Master[]>([]);
  protected codes: Record<string, FormControl<string>> = {};

  constructor() {
    effect(() => {
      const id = this.itemId();
      this.trade.warehouses().subscribe((w) => {
        const active = w.filter((x) => x.isActive);
        this.codes = Object.fromEntries(active.map((x) => [x.id, new FormControl('', { nonNullable: true })]));
        this.warehouses.set(active);
        this.api.get<Bin[]>(`inventory/items/${id}/bins`).subscribe((bins) => bins.forEach((b) => this.codes[b.warehouseId]?.setValue(b.binCode)));
      });
    });
  }

  protected save(): void {
    const body = Object.entries(this.codes).map(([warehouseId, c]) => ({ warehouseId, binCode: c.value.trim() || null }));
    this.api.put<Bin[]>(`inventory/items/${this.itemId()}/bins`, body).subscribe({
      next: (bins) => {
        bins.forEach((b) => this.codes[b.warehouseId]?.setValue(b.binCode));
        this.notify.success();
      },
      error: (e) => this.notify.error(e)
    });
  }
}

/** Look up a serial number: which item, where it is now, and every document that moved it. */
@Component({
  selector: 'app-serial-lookup',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'serial.title' | t }}</h1></div>
    <div class="search">
      <mat-form-field class="wide"><mat-icon matPrefix>qr_code_scanner</mat-icon><mat-label>{{ 'serial.no' | t }}</mat-label>
        <input matInput dir="ltr" [formControl]="query" (keydown.enter)="find()" /></mat-form-field>
      <button mat-flat-button (click)="find()">{{ 'app.search' | t }}</button>
    </div>
    @for (r of results(); track r.itemId) {
      <mat-card appearance="outlined" class="card">
        <h3><span class="ltr">{{ r.serialNo }}</span> · <a [routerLink]="['/inventory/items', r.itemId]"><span class="ltr">{{ r.sku }}</span> {{ r | bi }}</a>
          @if (r.inStock) { <span class="chip ok">{{ 'serial.inStock' | t }} · <span class="ltr">{{ r.warehouseCode }}</span></span> }
          @else { <span class="chip">{{ 'serial.out' | t }}</span> }</h3>
        <table class="line-editor">
          <thead><tr><th>{{ 'app.date' | t }}</th><th>{{ 'serial.document' | t }}</th><th>{{ 'batch.warehouse' | t }}</th><th class="num">{{ 'stock.quantity' | t }}</th></tr></thead>
          <tbody>
            @for (h of r.history; track $index) {
              <tr><td class="ltr">{{ h.date | d }}</td><td class="ltr">{{ h.sourceNo }}</td><td class="ltr">{{ h.warehouseCode }}</td>
                <td class="num ltr">{{ h.quantity > 0 ? '+1' : '−1' }}</td></tr>
            }
          </tbody>
        </table>
      </mat-card>
    }
  `,
  styles: `.search { display: flex; gap: 8px; align-items: baseline; flex-wrap: wrap; } .wide { min-width: min(320px, 100%); flex: 1; } .card { padding: 16px; margin-block-end: 12px; }`
})
export class SerialLookupPage {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly query = new FormControl('', { nonNullable: true });
  protected readonly results = signal<SerialInfo[]>([]);

  protected find(): void {
    const q = this.query.value.trim();
    if (!q) return;
    this.api.get<SerialInfo[]>(`inventory/serials/${encodeURIComponent(q)}`).subscribe({
      next: (r) => this.results.set(r),
      error: (e) => {
        this.results.set([]);
        this.notify.error(e);
      }
    });
  }
}
