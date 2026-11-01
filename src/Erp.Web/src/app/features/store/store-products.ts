import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { debounceTime, Observable } from 'rxjs';
import { CommerceApi, isNotSetUp, StoreListing } from '../../core/commerce.api';
import { NotifyService } from '../../core/notify.service';
import { TradeApi } from '../../core/trade.api';
import { Item } from '../../core/trade.models';
import { I18nService } from '../../core/i18n/i18n.service';
import { AuthImg } from '../../shared/auth-img';
import { SHARED } from '../../shared/shared';
import { StorePhotosData, StorePhotosDialog } from './store-photos';

/** Which inventory items the online store shows. Price and stock always come from the item; the store adds nothing to them. */
@Component({
  selector: 'app-store-products',
  imports: [...SHARED, AuthImg],
  template: `
    <div class="page-header">
      <h1>{{ 'store.products' | t }}</h1>
      <span class="spacer"></span>
      <span class="secondary">{{ 'store.inStoreCount' | t: { n: publishedCount() } }}</span>
    </div>
    <p class="secondary">{{ 'store.productsHint' | t }}</p>
    @if (notSetUp()) {
      <mat-card appearance="outlined" class="setup-banner">
        <mat-icon>storefront</mat-icon>
        <span>{{ 'store.setUpFirst' | t }}</span>
        <a mat-flat-button routerLink="/store/settings">{{ 'store.settings' | t }}</a>
      </mat-card>
    }
    <div class="tools">
      <mat-form-field subscriptSizing="dynamic" class="search">
        <mat-label>{{ 'app.search' | t }}</mat-label>
        <input matInput [formControl]="search" />
        <mat-icon matSuffix>search</mat-icon>
      </mat-form-field>
      <button mat-stroked-button [disabled]="!items().length || busy() || notSetUp()" (click)="publishShown()"><mat-icon>publish</mat-icon> {{ 'store.publishShown' | t }}</button>
    </div>
    <div class="table-wrap">
      <table class="products">
        <thead><tr><th>{{ 'item.sku' | t }}</th><th>{{ 'store.product' | t }}</th><th class="num">{{ 'store.price' | t }}</th><th>{{ 'store.inStore' | t }}</th><th>{{ 'store.photos' | t }}</th></tr></thead>
        <tbody>
          @for (i of items(); track i.id) {
            <tr>
              <td class="ltr">{{ i.sku }}</td>
              <td>{{ i | bi }}</td>
              <td class="num">{{ i.salesPrice ?? 0 | money }}</td>
              <td><mat-slide-toggle [checked]="published(i.id)" [disabled]="busy() || notSetUp() || i.salesPrice === null" (change)="toggle(i, $event.checked)"
                [attr.aria-label]="('store.inStore' | t) + ' ' + i.sku" /></td>
              <td>
                @if (listingOf(i.id); as l) {
                  <button mat-button class="photos" (click)="photos(i, l.id)" [attr.aria-label]="('store.photos' | t) + ' ' + i.sku">
                    @if (l.images.length) { <app-auth-img class="thumb" [source]="source(l.images[0])" /> } @else { <mat-icon>add_a_photo</mat-icon> }
                    <span class="ltr">{{ l.images.length }}</span>
                  </button>
                }
              </td>
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
    .setup-banner { display: flex; align-items: center; gap: 12px; padding: 14px 16px; margin-block-end: 16px; flex-wrap: wrap;
      background: var(--mat-sys-secondary-container); }
    .setup-banner span { flex: 1; min-width: 200px; }
    .products { border-collapse: collapse; width: 100%; }
    .products th, .products td { padding: 8px 12px; border-bottom: 1px solid var(--mat-sys-outline-variant); text-align: start; }
    .products .num { text-align: end; }
    .photos { min-width: 0; }
    .thumb { width: 36px; height: 36px; border-radius: 4px; display: inline-block; vertical-align: middle; margin-inline-end: 6px; }
  `
})
export class StoreProductsPage implements OnInit {
  private readonly commerce = inject(CommerceApi);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  private readonly dialogs = inject(MatDialog);
  private readonly i18n = inject(I18nService);
  private readonly sources = new Map<string, () => Observable<Blob>>();
  protected readonly search = new FormControl('', { nonNullable: true });
  protected readonly items = signal<Item[]>([]);
  protected readonly listings = signal<StoreListing[]>([]);
  protected readonly busy = signal(false);
  /** The store settings were never saved: nothing can be put in the store until they are. */
  protected readonly notSetUp = signal(false);
  protected readonly publishedCount = computed(() => this.listings().filter((l) => l.isPublished).length);
  private readonly publishedIds = computed(() => new Set(this.listings().filter((l) => l.isPublished).map((l) => l.itemId)));
  private readonly byItem = computed(() => new Map(this.listings().map((l) => [l.itemId, l])));

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

  /** Photos need the product in the store (listed once; it may be hidden again). */
  protected listingOf(itemId: string): StoreListing | undefined {
    return this.byItem().get(itemId);
  }

  /** One function per photo, so each thumbnail is fetched once. */
  protected source(imageId: string): () => Observable<Blob> {
    let f = this.sources.get(imageId);
    if (!f) this.sources.set(imageId, (f = () => this.commerce.imageFile(imageId)));
    return f;
  }

  protected photos(item: Item, listingId: string): void {
    const data: StorePhotosData = { listingId, name: this.i18n.isArabic() ? item.nameAr : item.nameEn };
    this.dialogs
      .open(StorePhotosDialog, { data, width: '640px', maxWidth: '95vw', autoFocus: false })
      .afterClosed()
      .subscribe((changed) => changed && this.loadListings());
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
    this.commerce.listings().subscribe({
      next: (list) => {
        this.notSetUp.set(false);
        this.listings.set(list);
      },
      error: (e) => (isNotSetUp(e) ? this.notSetUp.set(true) : this.notify.error(e))
    });
  }
}
