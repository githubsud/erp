import { Component, inject, signal } from '@angular/core';
import { FormArray, FormBuilder, FormControl, FormGroup, Validators } from '@angular/forms';
import { AuthService } from '../../core/auth.service';
import { NotifyService } from '../../core/notify.service';
import { SalesApi } from '../../core/sales.api';
import { PriceList } from '../../core/sales.models';
import { TradeApi } from '../../core/trade.api';
import { Item } from '../../core/trade.models';
import { ItemUnitsCache } from '../../core/item-units';
import { SHARED } from '../../shared/shared';

type PriceRow = FormGroup<{ itemId: FormControl<string>; uomId: FormControl<string | null>; unitPrice: FormControl<number | null> }>;

/** Special prices per customer group (wholesale, contractors…). Customers without a list pay the item's sales price. */
@Component({
  selector: 'app-price-lists',
  imports: [...SHARED],
  providers: [ItemUnitsCache],
  template: `
    <div class="page-header">
      <a mat-icon-button routerLink="/sales/customers"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
      <h1>{{ 'pl.title' | t }}</h1>
      @if (canManage) { <button mat-flat-button (click)="edit(null)"><mat-icon>add</mat-icon> {{ 'pl.new' | t }}</button> }
    </div>
    <p class="secondary">{{ 'pl.hint' | t }}</p>
    <div class="layout">
      <div class="lists">
        @for (p of lists(); track p.id) {
          <button mat-stroked-button class="list-btn" [class.active]="selected()?.id === p.id" (click)="edit(p)">
            <span class="ltr">{{ p.code }}</span> · {{ p | bi }} <span class="secondary">({{ p.items.length }})</span>
            @if (!p.isActive) { <span class="chip">{{ 'app.no' | t }} · {{ 'item.active' | t }}</span> }
          </button>
        }
        @if (lists().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
      </div>
      @if (editing()) {
        <mat-card appearance="outlined" class="form-card">
          <form [formGroup]="form" class="form-grid">
            <mat-form-field><mat-label>{{ 'tset.code' | t }}</mat-label><input matInput formControlName="code" dir="ltr" /></mat-form-field>
            <mat-slide-toggle formControlName="isActive">{{ 'item.active' | t }}</mat-slide-toggle>
            <mat-form-field><mat-label>{{ 'sup.nameAr' | t }}</mat-label><input matInput formControlName="nameAr" dir="rtl" /></mat-form-field>
            <mat-form-field><mat-label>{{ 'sup.nameEn' | t }}</mat-label><input matInput formControlName="nameEn" dir="ltr" /></mat-form-field>
          </form>
          <table class="line-editor">
            <thead><tr><th>{{ 'po.item' | t }}</th><th class="narrow">{{ 'units.unit' | t }}</th><th class="num">{{ 'pl.standard' | t }}</th>
              <th class="narrow">{{ 'pl.price' | t }}</th><th></th></tr></thead>
            <tbody>
              @for (row of rows.controls; track row; let i = $index) {
                <tr [formGroup]="row">
                  <td><mat-form-field><mat-select formControlName="itemId" (selectionChange)="itemChanged(row)">
                    @for (it of items(); track it.id) { <mat-option [value]="it.id"><span class="ltr">{{ it.sku }}</span> · {{ it | bi }}</mat-option> }
                  </mat-select></mat-form-field></td>
                  <td class="narrow"><mat-form-field><mat-select formControlName="uomId">
                    @for (u of units.of(row.controls.itemId.value); track u.uomId) { <mat-option [value]="u.isBase ? null : u.uomId">{{ u.uomCode }}</mat-option> }
                  </mat-select></mat-form-field></td>
                  <td class="num ltr">{{ standard(row.controls.itemId.value, row.controls.uomId.value) }}</td>
                  <td class="narrow"><mat-form-field><input matInput type="number" min="0" formControlName="unitPrice" dir="ltr" /></mat-form-field></td>
                  <td><button mat-icon-button type="button" (click)="rows.removeAt(i)"><mat-icon>delete</mat-icon></button></td>
                </tr>
              }
            </tbody>
          </table>
          <div class="actions bar">
            <button mat-button type="button" (click)="addRow()"><mat-icon>add</mat-icon> {{ 'doc.addLine' | t }}</button>
            <span class="spacer"></span>
            <button mat-button type="button" (click)="editing.set(false)">{{ 'app.cancel' | t }}</button>
            @if (canManage) { <button mat-flat-button (click)="save()" [disabled]="form.invalid || rows.invalid || busy()">{{ 'app.save' | t }}</button> }
          </div>
        </mat-card>
      }
    </div>
  `,
  styles: `
    .layout { display: grid; grid-template-columns: minmax(220px, 300px) 1fr; gap: 16px; align-items: start; }
    @media (max-width: 800px) { .layout { grid-template-columns: 1fr; } }
    .lists { display: flex; flex-direction: column; gap: 8px; }
    .list-btn { justify-content: flex-start; text-align: start; }
    .list-btn.active { background: var(--mat-sys-secondary-container); }
    .bar { align-items: center; margin-top: 8px; }
    .spacer { flex: 1; }
  `
})
export class PriceListsPage {
  private readonly sales = inject(SalesApi);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  private readonly fb = inject(FormBuilder).nonNullable;
  protected readonly canManage = inject(AuthService).can('sal.customers:manage');
  protected readonly lists = signal<PriceList[]>([]);
  protected readonly items = signal<Item[]>([]);
  protected readonly selected = signal<PriceList | null>(null);
  protected readonly editing = signal(false);
  protected readonly busy = signal(false);
  protected readonly form = this.fb.group({ code: ['', Validators.required], nameEn: [''], nameAr: [''], isActive: [true] });
  protected readonly rows = new FormArray<PriceRow>([]);
  protected readonly units = inject(ItemUnitsCache);

  constructor() {
    this.trade.items({ active: true, pageSize: 500 }).subscribe((r) => this.items.set(r.items));
    this.load();
  }

  /** The item list's price of one piece, or of one box (its own price, else pieces × size). */
  protected standard(itemId: string, uomId: string | null): string {
    const unit = uomId ? this.units.of(itemId).find((u) => u.uomId === uomId) : null;
    const piece = this.items().find((i) => i.id === itemId)?.salesPrice;
    const price = unit ? (unit.salesPrice ?? (piece == null ? null : piece * unit.factor)) : piece;
    return price === null || price === undefined ? '—' : price.toFixed(2);
  }

  protected itemChanged(row: PriceRow): void {
    row.controls.uomId.setValue(null);
    this.units.load(row.controls.itemId.value);
  }

  protected edit(p: PriceList | null): void {
    this.selected.set(p);
    this.form.reset({ code: p?.code ?? '', nameEn: p?.nameEn ?? '', nameAr: p?.nameAr ?? '', isActive: p?.isActive ?? true });
    this.rows.clear();
    for (const i of p?.items ?? []) {
      this.addRow(i.itemId, i.unitPrice, i.uomId ?? null);
      this.units.load(i.itemId);
    }
    if (!p) this.addRow();
    this.editing.set(true);
  }

  protected addRow(itemId = '', unitPrice: number | null = null, uomId: string | null = null): void {
    this.rows.push(this.fb.group({
      itemId: this.fb.control(itemId, Validators.required),
      uomId: this.fb.control<string | null>(uomId),
      unitPrice: this.fb.control<number | null>(unitPrice, [Validators.required, Validators.min(0)])
    }) as PriceRow);
  }

  protected save(): void {
    this.busy.set(true);
    const body = { ...this.form.getRawValue(), items: this.rows.getRawValue().map((r) => ({ itemId: r.itemId, uomId: r.uomId, unitPrice: Number(r.unitPrice) })) };
    this.sales.savePriceList(this.selected()?.id ?? null, body).subscribe({
      next: (p) => {
        this.busy.set(false);
        this.notify.success();
        this.selected.set(p);
        this.load();
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }

  private load(): void {
    this.sales.priceLists().subscribe((l) => this.lists.set(l));
  }
}
