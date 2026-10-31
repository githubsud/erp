import { Component, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
import { CommerceApi, StoreStatus } from '../../core/commerce.api';
import { NotifyService } from '../../core/notify.service';
import { TradeApi } from '../../core/trade.api';
import { Item, Master } from '../../core/trade.models';
import { SHARED } from '../../shared/shared';

/** The online store's settings: its address, names, cash on delivery, minimum order, delivery fee and whether it is open. */
@Component({
  selector: 'app-store-settings',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'store.settings' | t }}</h1></div>
    <mat-card appearance="outlined" class="form-card">
      <form [formGroup]="form" class="form-grid" (ngSubmit)="save()">
        <mat-form-field>
          <mat-label>{{ 'store.status' | t }}</mat-label>
          <mat-select formControlName="status">
            @for (s of statuses; track s) { <mat-option [value]="s">{{ 'store.statusOf.' + s | t }}</mat-option> }
          </mat-select>
          <mat-hint>{{ 'store.statusHint.' + form.controls.status.value | t }}</mat-hint>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'store.slug' | t }}</mat-label>
          <input matInput formControlName="slug" dir="ltr" />
          <mat-hint>{{ 'store.slugHint' | t }}</mat-hint>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'sup.nameAr' | t }}</mat-label>
          <input matInput formControlName="nameAr" dir="rtl" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'sup.nameEn' | t }}</mat-label>
          <input matInput formControlName="nameEn" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'store.phone' | t }}</mat-label>
          <input matInput formControlName="phone" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'store.host' | t }}</mat-label>
          <input matInput formControlName="host" dir="ltr" placeholder="shop.example.qa" />
          <mat-hint>{{ 'store.hostHint' | t }}</mat-hint>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'store.warehouse' | t }}</mat-label>
          <mat-select formControlName="warehouseId">
            @for (w of warehouses(); track w.id) { <mat-option [value]="w.id">{{ w | bi }}</mat-option> }
          </mat-select>
          <mat-hint>{{ 'store.warehouseHint' | t }}</mat-hint>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'store.minOrder' | t }}</mat-label>
          <input matInput type="number" min="0" formControlName="minOrder" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'store.deliveryFee' | t }}</mat-label>
          <input matInput type="number" min="0" formControlName="deliveryFee" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'store.deliveryItem' | t }}</mat-label>
          <mat-select formControlName="deliveryItemId">
            <mat-option [value]="null">—</mat-option>
            @for (i of services(); track i.id) { <mat-option [value]="i.id"><span class="ltr">{{ i.sku }}</span> · {{ i | bi }}</mat-option> }
          </mat-select>
          <mat-hint>{{ 'store.deliveryItemHint' | t }}</mat-hint>
        </mat-form-field>
        <mat-slide-toggle formControlName="codEnabled" class="full">{{ 'store.cod' | t }}</mat-slide-toggle>
        <div class="actions"><button mat-flat-button [disabled]="form.invalid || saving()">{{ 'app.save' | t }}</button></div>
      </form>
    </mat-card>
  `
})
export class StoreSettingsPage implements OnInit {
  private readonly commerce = inject(CommerceApi);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  private readonly fb = inject(FormBuilder);
  protected readonly statuses: StoreStatus[] = ['Draft', 'Open', 'Closed'];
  protected readonly warehouses = signal<Master[]>([]);
  protected readonly services = signal<Item[]>([]);
  protected readonly saving = signal(false);
  protected readonly form = this.fb.group({
    status: this.fb.nonNullable.control<StoreStatus>('Draft'),
    slug: this.fb.nonNullable.control('', [Validators.required, Validators.pattern(/^[a-z0-9]([a-z0-9-]*[a-z0-9])?$/), Validators.minLength(3)]),
    nameAr: this.fb.nonNullable.control('', Validators.required),
    nameEn: this.fb.nonNullable.control('', Validators.required),
    phone: this.fb.control<string | null>(null),
    host: this.fb.control<string | null>(null),
    warehouseId: this.fb.control<string | null>(null),
    minOrder: this.fb.nonNullable.control(0, [Validators.min(0)]),
    deliveryFee: this.fb.nonNullable.control(0, [Validators.min(0)]),
    deliveryItemId: this.fb.control<string | null>(null),
    codEnabled: this.fb.nonNullable.control(true)
  });

  ngOnInit(): void {
    this.trade.warehouses().subscribe((list) => this.warehouses.set(list.filter((w) => w.isActive)));
    this.trade.items({ type: 'Service', active: true, pageSize: 200 }).subscribe((page) => this.services.set(page.items));
    this.commerce.store().subscribe({ next: (s) => this.form.patchValue(s), error: (e) => this.notify.error(e) });
  }

  protected save(): void {
    this.saving.set(true);
    const v = this.form.getRawValue();
    this.commerce.saveStore({ ...v, phone: v.phone || null, host: v.host || null }).subscribe({
      next: (s) => {
        this.form.patchValue(s);
        this.saving.set(false);
        this.notify.success();
      },
      error: (e) => {
        this.saving.set(false);
        this.notify.error(e);
      }
    });
  }
}
