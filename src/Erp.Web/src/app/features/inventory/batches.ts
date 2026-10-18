import { Component, effect, inject, input, OnInit, signal } from '@angular/core';
import { FormControl } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

export interface BatchRow {
  batchId: string;
  itemId: string;
  sku: string;
  nameEn: string;
  nameAr: string;
  batchNo: string;
  expiryDate: string | null;
  warehouseId: string;
  warehouseCode: string;
  quantity: number;
  daysLeft: number | null;
}

export function expiryChip(daysLeft: number | null): string {
  return daysLeft === null ? 'info' : daysLeft < 0 ? 'bad' : daysLeft <= 30 ? 'warn' : 'ok';
}

/** Batches of one item with stock, soonest expiry first. */
@Component({
  selector: 'app-item-batches',
  imports: [...SHARED],
  template: `
    <table class="line-editor">
      <thead><tr><th>{{ 'batch.no' | t }}</th><th>{{ 'batch.warehouse' | t }}</th><th>{{ 'batch.expiry' | t }}</th><th class="num">{{ 'stock.quantity' | t }}</th></tr></thead>
      <tbody>
        @for (b of rows(); track b.batchId + b.warehouseId) {
          <tr>
            <td class="ltr">{{ b.batchNo }}</td>
            <td class="ltr">{{ b.warehouseCode }}</td>
            <td>@if (b.expiryDate) { <span class="ltr">{{ b.expiryDate | d }}</span>
              <span class="chip" [class]="chip(b.daysLeft)">{{ (b.daysLeft! < 0 ? 'batch.expired' : 'batch.daysLeft') | t: { n: b.daysLeft! } }}</span> } @else { — }</td>
            <td class="num ltr">{{ b.quantity }}</td>
          </tr>
        }
      </tbody>
    </table>
    @if (!rows().length) { <div class="empty">{{ 'app.none' | t }}</div> }
  `
})
export class ItemBatches {
  readonly itemId = input.required<string>();
  private readonly api = inject(ApiService);
  protected readonly rows = signal<BatchRow[]>([]);
  protected readonly chip = expiryChip;

  constructor() {
    effect(() => this.api.get<BatchRow[]>('inventory/batches', { itemId: this.itemId() }).subscribe((r) => this.rows.set(r)));
  }
}

/** Batches expired or expiring within the chosen number of days, across warehouses. */
@Component({
  selector: 'app-expiry-report',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'batch.report' | t }}</h1></div>
    <div class="toolbar-row">
      <mat-form-field>
        <mat-label>{{ 'batch.within' | t }}</mat-label>
        <mat-select [formControl]="days" (selectionChange)="load()">
          @for (d of [30, 60, 90, 180]; track d) { <mat-option [value]="d">{{ 'batch.days' | t: { n: d } }}</mat-option> }
        </mat-select>
      </mat-form-field>
    </div>
    <div class="table-wrap">
      <table class="line-editor">
        <thead><tr><th>{{ 'item.name' | t }}</th><th>{{ 'batch.no' | t }}</th><th>{{ 'batch.warehouse' | t }}</th><th>{{ 'batch.expiry' | t }}</th>
          <th class="num">{{ 'stock.quantity' | t }}</th></tr></thead>
        <tbody>
          @for (b of rows(); track b.batchId + b.warehouseId) {
            <tr>
              <td><a [routerLink]="['/inventory/items', b.itemId]"><span class="ltr">{{ b.sku }}</span></a> · {{ b | bi }}</td>
              <td class="ltr">{{ b.batchNo }}</td>
              <td class="ltr">{{ b.warehouseCode }}</td>
              <td><span class="ltr">{{ b.expiryDate | d }}</span>
                <span class="chip" [class]="chip(b.daysLeft)">{{ (b.daysLeft! < 0 ? 'batch.expired' : 'batch.daysLeft') | t: { n: b.daysLeft! } }}</span></td>
              <td class="num ltr">{{ b.quantity }}</td>
            </tr>
          }
        </tbody>
      </table>
      @if (!rows().length) { <p class="empty">{{ 'batch.noneExpiring' | t }}</p> }
    </div>
    <p class="secondary">{{ 'batch.reportHint' | t }}</p>
  `,
  styles: `.empty, .secondary { color: var(--mat-sys-on-surface-variant); padding: 12px 0; }`
})
export class ExpiryReportPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly rows = signal<BatchRow[]>([]);
  protected readonly days = new FormControl(90, { nonNullable: true });
  protected readonly chip = expiryChip;

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.api.get<BatchRow[]>('inventory/reports/expiry', { withinDays: this.days.value }).subscribe({
      next: (r) => this.rows.set(r),
      error: (e) => this.notify.error(e)
    });
  }
}
