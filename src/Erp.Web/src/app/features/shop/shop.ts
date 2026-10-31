import { Component, inject, input, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterOutlet } from '@angular/router';
import { debounceTime, distinctUntilChanged } from 'rxjs';
import { I18nService } from '../../core/i18n/i18n.service';
import { NotifyService } from '../../core/notify.service';
import { yearsText } from '../../core/part-search';
import { SHARED } from '../../shared/shared';
import { ShopCategory, ShopOrder, ShopProduct, ShopService } from './shop.service';

const SHOP_STYLES = `
  .spacer { flex: 1; }
  .secondary { color: var(--mat-sys-on-surface-variant); font-size: 13px; }
  .band { font-size: 12px; padding: 2px 8px; border-radius: 10px; white-space: nowrap; }
  .band.InStock { background: #d6f0dd; color: #1b6e3a; }
  .band.Low { background: #fde7c3; color: #6b4300; }
  .band.OutOfStock { background: #f9dedc; color: #8c1d18; }
  .price { font-weight: 600; white-space: nowrap; }
`;

/** Header (store name, language, cart) around the shop's pages; anonymous, Arabic first, made for phones. */
@Component({
  selector: 'app-shop-shell',
  imports: [...SHARED, RouterOutlet],
  template: `
    @if (shop.missing()) {
      <div class="missing">
        <mat-icon>storefront</mat-icon>
        <h1>{{ 'shop.missing' | t }}</h1>
      </div>
    } @else if (shop.store(); as s) {
      <header class="bar">
        <a class="name" [routerLink]="['/shop', shop.slug()]">{{ s | bi }}</a>
        <span class="spacer"></span>
        <button mat-button (click)="i18n.set(i18n.isArabic() ? 'en' : 'ar')">{{ 'app.language' | t }}</button>
        <a mat-icon-button [routerLink]="['/shop', shop.slug(), 'cart']" [attr.aria-label]="'shop.cart' | t" class="cart">
          <mat-icon>shopping_cart</mat-icon>
          @if (shop.count()) { <span class="count ltr">{{ shop.count() }}</span> }
        </a>
      </header>
      <main><router-outlet /></main>
      <footer class="secondary">
        @if (s.codEnabled) { <span><mat-icon>payments</mat-icon> {{ 'shop.codNote' | t }}</span> }
        @if (s.deliveryFee) { <span>{{ 'shop.deliveryFee' | t }} <b class="ltr">{{ s.deliveryFee | money }}</b></span> }
        @if (s.phone) { <a class="ltr" [href]="'tel:' + s.phone">{{ s.phone }}</a> }
      </footer>
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: `
    :host { display: block; min-height: 100vh; background: var(--mat-sys-surface); }
    .spacer { flex: 1; }
    .bar { position: sticky; top: 0; z-index: 2; display: flex; align-items: center; gap: 4px; padding: 8px 16px;
      background: var(--mat-sys-surface-container); border-bottom: 1px solid var(--mat-sys-outline-variant); }
    .name { font: var(--mat-sys-title-large); color: inherit; text-decoration: none; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .cart { position: relative; }
    .count { position: absolute; top: 2px; inset-inline-end: 2px; min-width: 18px; height: 18px; border-radius: 9px; font-size: 11px; line-height: 18px;
      text-align: center; background: var(--mat-sys-primary); color: var(--mat-sys-on-primary); }
    main { max-width: 1100px; margin: 0 auto; padding: 16px; box-sizing: border-box; }
    footer { display: flex; flex-wrap: wrap; gap: 16px; justify-content: center; padding: 24px 16px; color: var(--mat-sys-on-surface-variant); font-size: 13px; }
    footer mat-icon { font-size: 16px; width: 16px; height: 16px; vertical-align: middle; }
    .missing { display: grid; place-items: center; gap: 8px; padding: 64px 16px; text-align: center; }
    .missing mat-icon { font-size: 48px; width: 48px; height: 48px; color: var(--mat-sys-on-surface-variant); }
  `
})
export class ShopShell implements OnInit {
  protected readonly shop = inject(ShopService);
  protected readonly i18n = inject(I18nService);
  private readonly notify = inject(NotifyService);
  readonly slug = input.required<string>();

  ngOnInit(): void {
    this.shop.open(this.slug().toLowerCase()).catch((e) => this.notify.error(e));
  }
}

/** Search (product, part number or car, e.g. "corolla 2015"), categories and the products. */
@Component({
  selector: 'app-shop-home',
  imports: [...SHARED],
  template: `
    <mat-form-field class="search" subscriptSizing="dynamic">
      <mat-icon matPrefix>search</mat-icon>
      <input matInput [formControl]="search" [placeholder]="'shop.searchHint' | t" type="search" />
    </mat-form-field>
    @if (categories().length) {
      <div class="cats">
        <button mat-stroked-button [class.on]="!category()" (click)="pick(null)">{{ 'shop.all' | t }}</button>
        @for (c of categories(); track c.slug) {
          <button mat-stroked-button [class.on]="category() === c.slug" (click)="pick(c.slug)">{{ c | bi }}</button>
        }
      </div>
    }
    @if (loading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (!loading() && !products().length) { <p class="secondary empty">{{ 'shop.noProducts' | t }}</p> }
    <div class="grid">
      @for (p of products(); track p.slug) {
        <mat-card appearance="outlined" class="product">
          <a class="pic" [routerLink]="['/shop', shop.slug(), 'p', p.slug]" [attr.aria-label]="i18n.isArabic() ? p.titleAr : p.titleEn"><mat-icon>inventory_2</mat-icon></a>
          <a class="title" [routerLink]="['/shop', shop.slug(), 'p', p.slug]">{{ i18n.isArabic() ? p.titleAr : p.titleEn }}</a>
          @if (p.brand) { <span class="secondary">{{ p.brand }}</span> }
          <div class="row">
            <span class="price ltr">{{ p.price | money }}</span><span class="secondary ltr">/ {{ p.uomCode }}</span>
            <span class="spacer"></span>
            <span class="band" [class]="p.availability">{{ 'shop.band.' + p.availability | t }}</span>
          </div>
          <button mat-flat-button [disabled]="p.availability === 'OutOfStock' || busy() === p.slug" (click)="add(p)">
            <mat-icon>add_shopping_cart</mat-icon> {{ 'shop.add' | t }}
          </button>
        </mat-card>
      }
    </div>
    @if (total() > products().length) {
      <div class="more"><button mat-stroked-button (click)="more()">{{ 'shop.more' | t }}</button></div>
    }
  `,
  styles:
    SHOP_STYLES +
    `
    .search { width: 100%; }
    .cats { display: flex; gap: 8px; overflow-x: auto; padding-block: 12px; }
    .cats button { flex: none; }
    .cats .on { background: var(--mat-sys-secondary-container); }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(160px, 1fr)); gap: 12px; }
    .product { padding: 12px; display: flex; flex-direction: column; gap: 6px; }
    .pic { display: grid; place-items: center; aspect-ratio: 4 / 3; border-radius: 8px; background: var(--mat-sys-surface-container); color: var(--mat-sys-on-surface-variant); }
    .pic mat-icon { font-size: 40px; width: 40px; height: 40px; }
    .title { color: inherit; text-decoration: none; font-weight: 500; line-height: 1.3; }
    .row { display: flex; align-items: center; gap: 4px; flex-wrap: wrap; }
    .more { display: flex; justify-content: center; padding: 16px; }
    .empty { text-align: center; padding: 32px; }
  `
})
export class ShopHomePage implements OnInit {
  protected readonly shop = inject(ShopService);
  protected readonly i18n = inject(I18nService);
  private readonly notify = inject(NotifyService);
  protected readonly search = new FormControl('', { nonNullable: true });
  protected readonly categories = signal<ShopCategory[]>([]);
  protected readonly category = signal<string | null>(null);
  protected readonly products = signal<ShopProduct[]>([]);
  protected readonly total = signal(0);
  protected readonly loading = signal(false);
  protected readonly busy = signal<string | null>(null);
  private page = 1;

  constructor() {
    this.search.valueChanges.pipe(debounceTime(350), distinctUntilChanged(), takeUntilDestroyed()).subscribe(() => this.load());
  }

  ngOnInit(): void {
    this.shop.categories().subscribe({ next: (c) => this.categories.set(c), error: () => this.categories.set([]) });
    this.load();
  }

  protected pick(category: string | null): void {
    this.category.set(category);
    this.load();
  }

  protected load(append = false): void {
    this.page = append ? this.page + 1 : 1;
    this.loading.set(true);
    this.shop.catalog({ search: this.search.value.trim(), category: this.category() ?? undefined, page: this.page, pageSize: 24 }).subscribe({
      next: (r) => {
        this.products.set(append ? [...this.products(), ...r.items] : r.items);
        this.total.set(r.total);
        this.loading.set(false);
      },
      error: (e) => {
        this.loading.set(false);
        this.notify.error(e);
      }
    });
  }

  protected more(): void {
    this.load(true);
  }

  protected async add(p: ShopProduct): Promise<void> {
    this.busy.set(p.slug);
    try {
      await this.shop.add(p.slug, p.uomCode);
      this.notify.info(this.i18n.t('shop.added', { name: this.i18n.isArabic() ? p.titleAr : p.titleEn }));
    } catch (e) {
      this.notify.error(e);
    } finally {
      this.busy.set(null);
    }
  }
}

/** One product: price per unit (piece or box), the cars it fits, quantity and add to cart. */
@Component({
  selector: 'app-shop-product',
  imports: [...SHARED],
  template: `
    @if (product(); as p) {
      <a mat-button [routerLink]="['/shop', shop.slug()]"><mat-icon class="flip-rtl">arrow_back</mat-icon> {{ 'shop.back' | t }}</a>
      <div class="layout">
        <div class="pic"><mat-icon>inventory_2</mat-icon></div>
        <div class="info">
          <h1>{{ i18n.isArabic() ? p.titleAr : p.titleEn }}</h1>
          @if (p.brand) { <p class="secondary">{{ p.brand }}</p> }
          <span class="band" [class]="p.availability">{{ 'shop.band.' + p.availability | t }}</span>
          @if (p.units.length > 1) {
            <div class="units" role="radiogroup" [attr.aria-label]="'shop.unit' | t">
              @for (u of p.units; track u.uomCode) {
                <button mat-stroked-button role="radio" [attr.aria-checked]="unit() === u.uomCode" [class.on]="unit() === u.uomCode" (click)="unit.set(u.uomCode)">
                  <span class="ltr">{{ u.uomCode }}</span> · <span class="price ltr">{{ u.price | money }}</span>
                </button>
              }
            </div>
          } @else {
            <p><span class="price ltr big">{{ p.price | money }}</span> <span class="secondary ltr">/ {{ p.uomCode }}</span></p>
          }
          <div class="qty">
            <button mat-icon-button (click)="quantity.set(Math.max(1, quantity() - 1))" [attr.aria-label]="'shop.less' | t"><mat-icon>remove</mat-icon></button>
            <span class="ltr n">{{ quantity() }}</span>
            <button mat-icon-button (click)="quantity.set(quantity() + 1)" [attr.aria-label]="'shop.moreQty' | t"><mat-icon>add</mat-icon></button>
            <button mat-flat-button [disabled]="p.availability === 'OutOfStock' || busy()" (click)="add(p)">
              <mat-icon>add_shopping_cart</mat-icon> {{ 'shop.add' | t }}
            </button>
          </div>
          @if (p.fits.length) {
            <h2>{{ 'shop.fits' | t }}</h2>
            <ul class="fits">
              @for (f of p.fits; track $index) { <li class="ltr">{{ f.make }} {{ f.model }} {{ years(f) }} {{ f.engine ?? '' }}</li> }
            </ul>
          }
          @if (i18n.isArabic() ? p.descriptionAr : p.descriptionEn; as d) { <p class="desc">{{ d }}</p> }
        </div>
      </div>
    } @else if (notFound()) {
      <p class="secondary">{{ 'shop.noProduct' | t }}</p>
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles:
    SHOP_STYLES +
    `
    .layout { display: grid; gap: 16px; grid-template-columns: minmax(0, 1fr); }
    @media (min-width: 720px) { .layout { grid-template-columns: 1fr 1fr; } }
    .pic { display: grid; place-items: center; aspect-ratio: 4 / 3; border-radius: 12px; background: var(--mat-sys-surface-container); color: var(--mat-sys-on-surface-variant); }
    .pic mat-icon { font-size: 64px; width: 64px; height: 64px; }
    .info { display: flex; flex-direction: column; gap: 10px; align-items: flex-start; }
    h1 { margin: 0; font: var(--mat-sys-headline-small); }
    h2 { margin: 8px 0 0; font: var(--mat-sys-title-medium); }
    .big { font-size: 22px; }
    .units { display: flex; gap: 8px; flex-wrap: wrap; }
    .units .on { background: var(--mat-sys-secondary-container); }
    .qty { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
    .qty .n { min-width: 24px; text-align: center; font-weight: 600; }
    .fits { margin: 0; padding-inline-start: 20px; }
    .desc { white-space: pre-line; }
  `
})
export class ShopProductPage implements OnInit {
  protected readonly shop = inject(ShopService);
  protected readonly i18n = inject(I18nService);
  private readonly notify = inject(NotifyService);
  private readonly router = inject(Router);
  protected readonly Math = Math;
  readonly product$ = input.required<string>({ alias: 'product' });
  protected readonly product = signal<ShopProduct | null>(null);
  protected readonly notFound = signal(false);
  protected readonly unit = signal('');
  protected readonly quantity = signal(1);
  protected readonly busy = signal(false);
  protected readonly years = yearsText;

  ngOnInit(): void {
    this.shop.product(this.product$()).subscribe({
      next: (p) => {
        this.product.set(p);
        this.unit.set(p.uomCode);
      },
      error: () => this.notFound.set(true)
    });
  }

  protected async add(p: ShopProduct): Promise<void> {
    this.busy.set(true);
    try {
      await this.shop.add(p.slug, this.unit() || p.uomCode, this.quantity());
      void this.router.navigate(['/shop', this.shop.slug(), 'cart']);
    } catch (e) {
      this.notify.error(e);
    } finally {
      this.busy.set(false);
    }
  }
}

/** The cart: change quantities, see what cannot be supplied, the delivery fee and the minimum order. */
@Component({
  selector: 'app-shop-cart',
  imports: [...SHARED],
  template: `
    <h1>{{ 'shop.cart' | t }}</h1>
    @if (shop.cart(); as c) {
      @if (!c.lines.length) {
        <p class="secondary">{{ 'shop.emptyCart' | t }}</p>
      } @else {
        <div class="lines">
          @for (l of c.lines; track l.productSlug + l.uomCode) {
            <div class="line" [class.off]="!l.available">
              <div class="what">
                <b>{{ i18n.isArabic() ? l.titleAr : l.titleEn }}</b>
                <span class="secondary"><span class="ltr">{{ l.unitPrice | money }}</span> / <span class="ltr">{{ l.uomCode }}</span></span>
                @if (!l.available) { <span class="warn">{{ 'shop.unavailable' | t }}</span> }
                @else if (!l.enough) { <span class="warn">{{ 'shop.notEnough' | t }}</span> }
              </div>
              <div class="qty">
                <button mat-icon-button (click)="set(l.productSlug, l.uomCode, l.quantity - 1)" [attr.aria-label]="'shop.less' | t"><mat-icon>remove</mat-icon></button>
                <span class="ltr n">{{ l.quantity }}</span>
                <button mat-icon-button [disabled]="!l.available" (click)="set(l.productSlug, l.uomCode, l.quantity + 1)" [attr.aria-label]="'shop.moreQty' | t"><mat-icon>add</mat-icon></button>
              </div>
              <span class="price ltr">{{ l.amount | money }}</span>
            </div>
          }
        </div>
        <div class="sum">
          <div><span>{{ 'shop.subtotal' | t }}</span><span class="ltr">{{ c.subtotal | money }}</span></div>
          @if (c.deliveryFee) { <div><span>{{ 'shop.deliveryFee' | t }}</span><span class="ltr">{{ c.deliveryFee | money }}</span></div> }
          <div class="total"><span>{{ 'app.total' | t }}</span><span class="ltr">{{ c.total | money }}</span></div>
        </div>
        @if (!c.meetsMinimum) { <p class="warn">{{ 'shop.minimum' | t: { amount: (c.minOrder | money) } }}</p> }
        <button mat-flat-button class="go" [disabled]="!c.canCheckout" [routerLink]="['/shop', shop.slug(), 'checkout']">{{ 'shop.checkout' | t }}</button>
      }
    } @else {
      <p class="secondary">{{ 'shop.emptyCart' | t }}</p>
    }
    <a mat-button [routerLink]="['/shop', shop.slug()]"><mat-icon class="flip-rtl">arrow_back</mat-icon> {{ 'shop.keepShopping' | t }}</a>
  `,
  styles:
    SHOP_STYLES +
    `
    h1 { font: var(--mat-sys-headline-small); }
    .lines { display: flex; flex-direction: column; }
    .line { display: flex; align-items: center; gap: 8px; padding-block: 10px; border-bottom: 1px solid var(--mat-sys-outline-variant); flex-wrap: wrap; }
    .line.off { opacity: 0.6; }
    .what { flex: 1 1 160px; display: flex; flex-direction: column; gap: 2px; }
    .qty { display: flex; align-items: center; }
    .qty .n { min-width: 24px; text-align: center; font-weight: 600; }
    .warn { color: #8c1d18; font-size: 13px; }
    .sum { margin-block: 16px; display: flex; flex-direction: column; gap: 6px; max-width: 360px; margin-inline-start: auto; }
    .sum div { display: flex; justify-content: space-between; }
    .sum .total { font-weight: 600; font-size: 18px; }
    .go { width: 100%; margin-block: 8px; height: 48px; }
  `
})
export class ShopCartPage implements OnInit {
  protected readonly shop = inject(ShopService);
  protected readonly i18n = inject(I18nService);
  private readonly notify = inject(NotifyService);

  ngOnInit(): void {
    this.shop.loadCart().catch((e) => this.notify.error(e));
  }

  protected set(productSlug: string, uomCode: string, quantity: number): void {
    this.shop.setQuantity(productSlug, uomCode, Math.max(0, quantity)).catch((e) => this.notify.error(e));
  }
}

/** Cash on delivery to a Qatar address (the blue plate: zone, street, building). */
@Component({
  selector: 'app-shop-checkout',
  imports: [...SHARED],
  template: `
    <h1>{{ 'shop.checkout' | t }}</h1>
    @if (shop.cart(); as c) { <p>{{ 'app.total' | t }}: <b class="ltr">{{ c.total | money }}</b> · {{ 'shop.codNote' | t }}</p> }
    <form [formGroup]="form" class="form" (ngSubmit)="place()">
      <mat-form-field><mat-label>{{ 'shop.name' | t }}</mat-label><input matInput formControlName="customerName" autocomplete="name" /></mat-form-field>
      <mat-form-field><mat-label>{{ 'shop.phone' | t }}</mat-label>
        <input matInput formControlName="phone" type="tel" dir="ltr" autocomplete="tel" placeholder="+974 5555 5555" /></mat-form-field>
      <p class="secondary">{{ 'shop.plateHint' | t }}</p>
      <div class="plate">
        <mat-form-field><mat-label>{{ 'shop.zone' | t }}</mat-label><input matInput formControlName="zone" inputmode="numeric" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'shop.street' | t }}</mat-label><input matInput formControlName="street" inputmode="numeric" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'shop.building' | t }}</mat-label><input matInput formControlName="building" inputmode="numeric" dir="ltr" /></mat-form-field>
      </div>
      <mat-form-field><mat-label>{{ 'shop.unitNo' | t }}</mat-label><input matInput formControlName="unit" dir="ltr" /></mat-form-field>
      <mat-form-field><mat-label>{{ 'shop.notes' | t }}</mat-label><textarea matInput formControlName="notes" rows="2"></textarea></mat-form-field>
      <button mat-stroked-button type="button" (click)="locate()"><mat-icon>my_location</mat-icon>
        {{ (located() ? 'shop.located' : 'shop.locate') | t }}</button>
      <button mat-flat-button class="go" [disabled]="form.invalid || busy()">{{ 'shop.place' | t }}</button>
    </form>
  `,
  styles:
    SHOP_STYLES +
    `
    h1 { font: var(--mat-sys-headline-small); }
    .form { display: flex; flex-direction: column; gap: 4px; max-width: 520px; }
    .plate { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 8px; }
    .go { height: 48px; margin-block: 12px; }
  `
})
export class ShopCheckoutPage {
  protected readonly shop = inject(ShopService);
  private readonly notify = inject(NotifyService);
  private readonly router = inject(Router);
  protected readonly busy = signal(false);
  protected readonly located = signal(false);
  private readonly fb = inject(FormBuilder).nonNullable;
  protected readonly form = this.fb.group({
    customerName: ['', [Validators.required, Validators.maxLength(200)]],
    phone: ['', [Validators.required, Validators.pattern(/^[+\d\s-]{8,20}$/)]],
    zone: ['', [Validators.required, Validators.maxLength(10)]],
    street: ['', [Validators.required, Validators.maxLength(10)]],
    building: ['', [Validators.required, Validators.maxLength(10)]],
    unit: [''],
    notes: [''],
    latitude: [null as number | null],
    longitude: [null as number | null]
  });

  protected locate(): void {
    navigator.geolocation?.getCurrentPosition(
      (p) => {
        this.form.patchValue({ latitude: Math.round(p.coords.latitude * 1e6) / 1e6, longitude: Math.round(p.coords.longitude * 1e6) / 1e6 });
        this.located.set(true);
      },
      () => this.located.set(false)
    );
  }

  protected async place(): Promise<void> {
    this.busy.set(true);
    const v = this.form.getRawValue();
    try {
      const orderNo = await this.shop.checkout({ ...v, unit: v.unit || null, notes: v.notes || null });
      void this.router.navigate(['/shop', this.shop.slug(), 'order', orderNo]);
    } catch (e) {
      this.notify.error(e);
      await this.shop.loadCart().catch(() => undefined);
    } finally {
      this.busy.set(false);
    }
  }
}

/** The shopper's order: received, delivered or cancelled, with what was ordered. */
@Component({
  selector: 'app-shop-order',
  imports: [...SHARED],
  template: `
    @if (order(); as o) {
      <div class="done">
        <mat-icon>{{ o.status === 'Delivered' ? 'task_alt' : o.status === 'Cancelled' ? 'cancel' : 'schedule' }}</mat-icon>
        <h1>{{ 'shop.status.' + o.status | t }}</h1>
        <p>{{ 'shop.orderNo' | t }} <b class="ltr">{{ o.orderNo }}</b></p>
      </div>
      <table class="lines">
        @for (l of o.lines; track $index) {
          <tr><td>{{ i18n.isArabic() ? l.titleAr : l.titleEn }}</td><td class="ltr">{{ l.quantity }} {{ l.uomCode }}</td><td class="num ltr">{{ l.amount | money }}</td></tr>
        }
        @if (o.deliveryFee) { <tr><td>{{ 'shop.deliveryFee' | t }}</td><td></td><td class="num ltr">{{ o.deliveryFee | money }}</td></tr> }
        <tr class="total"><td>{{ 'app.total' | t }}</td><td></td><td class="num ltr">{{ o.total | money }}</td></tr>
      </table>
      <p class="secondary">{{ 'shop.codNote' | t }}</p>
    } @else if (missing()) {
      <p class="secondary">{{ 'shop.noOrder' | t }}</p>
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
    <a mat-button [routerLink]="['/shop', shop.slug()]"><mat-icon class="flip-rtl">arrow_back</mat-icon> {{ 'shop.keepShopping' | t }}</a>
  `,
  styles:
    SHOP_STYLES +
    `
    .done { text-align: center; padding-block: 16px; }
    .done mat-icon { font-size: 56px; width: 56px; height: 56px; color: #1b6e3a; }
    .done h1 { font: var(--mat-sys-headline-small); margin: 8px 0; }
    .lines { width: 100%; border-collapse: collapse; }
    .lines td { padding: 8px 0; border-bottom: 1px solid var(--mat-sys-outline-variant); }
    .lines .num { text-align: end; white-space: nowrap; }
    .lines .total td { font-weight: 600; border-bottom: 0; }
  `
})
export class ShopOrderPage implements OnInit {
  protected readonly shop = inject(ShopService);
  protected readonly i18n = inject(I18nService);
  private readonly route = inject(ActivatedRoute);
  protected readonly order = signal<ShopOrder | null>(null);
  protected readonly missing = signal(false);

  ngOnInit(): void {
    this.shop.order(this.route.snapshot.paramMap.get('orderNo') ?? '').subscribe({
      next: (o) => this.order.set(o),
      error: () => this.missing.set(true)
    });
  }
}

