import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl } from '@angular/forms';
import { debounceTime } from 'rxjs';
import { CommerceApi, StoreListing } from '../../core/commerce.api';
import { NotifyService } from '../../core/notify.service';
import { TradeApi } from '../../core/trade.api';
import { Item } from '../../core/trade.models';
import { SHARED } from '../../shared/shared';

/** Which inventory items the online store shows. Price and stock always come from the item; the store adds nothing to them. */
@Component({
  selector: 'app-store-products',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'store.products' | t }}</h1>
      <span class="spacer"></span>
      <span class="secondary">{{ 'store.inStoreCount' | t: { n: publishedCount() } }}</span>
    </div>
    <p class="secondary">{{ 'store.productsHint' | t }}</p>
    <div class="tools">
      <mat-form-field subscriptSizing="dynamic" class="search">
        <mat-label>{{ 'app.search' | t }}</mat-label>
        <input matInput [formControl]="search" />
        <mat-icon matSuffix>search</mat-icon>
      </mat-form-field>
      <button mat-stroked-button [disabled]="!items().length || busy()" (click)="publishShown()"><mat-icon>publish</mat-icon> {{ 'store.publishShown' | t }}</button>
    </div>
    <div class="table-wrap">
      <table class="products">
        <thead><tr><th>{{ 'item.sku' | t }}</th><th>{{ 'store.product' | t }}</th><th class="num">{{ 'store.price' | t }}</th><th>{{ 'store.inStore' | t }}</th></tr></thead>
        <tbody>
          @for (i of items(); track i.id) {
            <tr>
              <td class="ltr">{{ i.sku }}</td>
              <td>{{ i | bi }}</td>
              <td class="num">{{ i.salesPrice ?? 0 | money }}</td>
              <td><mat-slide-toggle [checked]="published(i.id)" [disabled]="busy() || i.salesPrice === null" (change)="toggle(i, $event.checked)"
                [attr.aria-label]="('store.inStore' | t) + ' ' + i.sku" /></td>
            </tr>
          }
        </tbody>
      </table>
    </div>
  `,
  styles: `
    .spacer { flex: 1; }
    .page-header { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 13px; }
    .tools { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; margin-block-end: 12px; }
    .search { min-width: 260px; }
    .products { border-collapse: collapse; width: 100%; }
    .products th, .products td { padding: 8px 12px; border-bottom: 1px solid var(--mat-sys-outline-variant); text-align: start; }
    .products .num { text-align: end; }
  `
})
export class StoreProductsPage implements OnInit {
  private readonly commerce = inject(CommerceApi);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  protected readonly search = new FormControl('', { nonNullable: true });
  protected readonly items = signal<Item[]>([]);
  protected readonly listings = signal<StoreListing[]>([]);
  protected readonly busy = signal(false);
  protected readonly publishedCount = computed(() => this.listings().filter((l) => l.isPublished).length);
  private readonly publishedIds = computed(() => new Set(this.listings().filter((l) => l.isPublished).map((l) => l.itemId)));

  constructor() {
    this.search.valueChanges.pipe(debounceTime(300), takeUntilDestroyed()).subscribe(() => this.loadItems());
  }

  ngOnInit(): void {
    this.loadItems();
    this.loadListings();
  }

  protected published(itemId: string): boolean {
    return this.publishedIds().has(itemId);
  }

  protected toggle(item: Item, publish: boolean): void {
    this.apply([item.id], publish);
  }

  protected publishShown(): void {
    this.apply(this.items().filter((i) => i.salesPrice !== null).map((i) => i.id), true);
  }

  private apply(itemIds: string[], publish: boolean): void {
    this.busy.set(true);
    this.commerce.publish(itemIds, publish).subscribe({
      next: () => {
        this.busy.set(false);
        this.loadListings();
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
        this.loadListings();
      }
    });
  }

  private loadItems(): void {
    this.trade.items({ search: this.search.value || undefined, active: true, pageSize: 100 }).subscribe({
      next: (page) => this.items.set(page.items),
      error: (e) => this.notify.error(e)
    });
  }

  private loadListings(): void {
    this.commerce.listings().subscribe({ next: (list) => this.listings.set(list), error: (e) => this.notify.error(e) });
  }
}
