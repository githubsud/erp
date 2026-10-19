import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormControl } from '@angular/forms';
import { Router } from '@angular/router';
import { forkJoin } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { todayIso } from '../../core/dates';
import { NotifyService } from '../../core/notify.service';
import { TradeApi } from '../../core/trade.api';
import { Master, Supplier } from '../../core/trade.models';
import { SHARED } from '../../shared/shared';

interface Suggestion {
  itemId: string;
  sku: string;
  nameEn: string;
  nameAr: string;
  uomCode: string;
  onHand: number;
  onOrder: number;
  reorderLevel: number;
  suggestedQuantity: number;
  supplierId: string | null;
  unitPrice: number | null;
}

interface Row extends Suggestion {
  pick: FormControl<boolean>;
  qty: FormControl<number>;
  supplier: FormControl<string>;
  price: FormControl<number>;
}

/** Items at or below their reorder level, ready to become draft purchase orders (one per supplier). */
@Component({
  selector: 'app-reorder',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'reorder.title' | t }}</h1>
      <span class="spacer"></span>
      @if (canOrder) {
        <mat-form-field><mat-label>{{ 'po.warehouse' | t }}</mat-label>
          <mat-select [formControl]="warehouse">@for (w of warehouses(); track w.id) { <mat-option [value]="w.id">{{ w | bi }}</mat-option> }</mat-select></mat-form-field>
        <button mat-flat-button [disabled]="!ready()" (click)="create()"><mat-icon>add_shopping_cart</mat-icon> {{ 'reorder.create' | t }}</button>
      }
    </div>
    <p class="secondary">{{ 'reorder.hint' | t }}</p>
    <table class="line-editor">
      <thead><tr><th></th><th>{{ 'src.item' | t }}</th><th class="num">{{ 'reorder.onHand' | t }}</th><th class="num">{{ 'reorder.onOrder' | t }}</th>
        <th class="num">{{ 'item.reorderLevel' | t }}</th><th class="narrow">{{ 'po.qty' | t }}</th><th>{{ 'po.supplier' | t }}</th><th class="narrow">{{ 'reorder.price' | t }}</th></tr></thead>
      <tbody>
        @for (r of rows(); track r.itemId) {
          <tr>
            <td><mat-checkbox [formControl]="r.pick" (change)="touch()" /></td>
            <td><span class="ltr secondary">{{ r.sku }}</span> {{ r | bi }}</td>
            <td class="num ltr">{{ r.onHand }} <span class="secondary">{{ r.uomCode }}</span></td>
            <td class="num ltr">{{ r.onOrder }}</td>
            <td class="num ltr">{{ r.reorderLevel }}</td>
            <td class="narrow"><mat-form-field><input matInput type="number" min="0" [formControl]="r.qty" dir="ltr" /></mat-form-field></td>
            <td><mat-form-field><mat-select [formControl]="r.supplier" (selectionChange)="touch()">@for (s of suppliers(); track s.id) {
              <mat-option [value]="s.id">{{ s | bi }}</mat-option> }</mat-select></mat-form-field></td>
            <td class="narrow"><mat-form-field><input matInput type="number" min="0" [formControl]="r.price" dir="ltr" /></mat-form-field></td>
          </tr>
        }
      </tbody>
    </table>
    @if (!rows().length) { <p class="secondary">{{ 'reorder.none' | t }}</p> }
  `,
  styles: `.spacer { flex: 1; } .secondary { color: var(--mat-sys-on-surface-variant); }`
})
export class ReorderPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  private readonly router = inject(Router);
  protected readonly canOrder = inject(AuthService).can('pur.orders:manage');
  protected readonly rows = signal<Row[]>([]);
  protected readonly suppliers = signal<Supplier[]>([]);
  protected readonly warehouses = signal<Master[]>([]);
  protected readonly warehouse = new FormControl('', { nonNullable: true });
  private readonly version = signal(0);
  protected readonly ready = computed(() => {
    this.version();
    const picked = this.rows().filter((r) => r.pick.value);
    return picked.length > 0 && picked.every((r) => r.supplier.value && Number(r.qty.value) > 0) && !!this.warehouse.value;
  });

  ngOnInit(): void {
    forkJoin([this.trade.suppliers(), this.trade.warehouses()]).subscribe(([s, w]) => {
      this.suppliers.set(s.filter((x) => x.isActive && x.currency === 'QAR'));
      this.warehouses.set(w.filter((x) => x.isActive));
      this.warehouse.setValue(w.find((x) => x.code === 'MAIN')?.id ?? w[0]?.id ?? '');
    });
    this.load();
  }

  private load(): void {
    this.api.get<Suggestion[]>('purchasing/reorder-suggestions').subscribe({
      next: (list) =>
        this.rows.set(list.map((s) => ({
          ...s,
          pick: new FormControl(!!s.supplierId, { nonNullable: true }),
          qty: new FormControl(s.suggestedQuantity, { nonNullable: true }),
          supplier: new FormControl(s.supplierId ?? '', { nonNullable: true }),
          price: new FormControl(s.unitPrice ?? 0, { nonNullable: true })
        }))),
      error: (e) => this.notify.error(e)
    });
  }

  protected touch(): void {
    this.version.update((v) => v + 1);
  }

  protected create(): void {
    const lines = this.rows().filter((r) => r.pick.value)
      .map((r) => ({ itemId: r.itemId, supplierId: r.supplier.value, quantity: Number(r.qty.value), unitPrice: Number(r.price.value) || 0 }));
    this.api.post<{ purchaseOrderIds: string[] }>('purchasing/reorder-suggestions/orders', { warehouseId: this.warehouse.value, orderDate: todayIso(), lines })
      .subscribe({
        next: (r) => {
          this.notify.success();
          if (r.purchaseOrderIds.length === 1) void this.router.navigate(['/purchasing/orders', r.purchaseOrderIds[0]]);
          else this.load();
        },
        error: (e) => this.notify.error(e)
      });
  }
}
