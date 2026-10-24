import { downloadCsv } from '../../shared/export';
import { Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { Router } from '@angular/router';
import { debounceTime, distinctUntilChanged, merge } from 'rxjs';
import { AuthService } from '../../core/auth.service';
import { NotifyService } from '../../core/notify.service';
import { TradeApi } from '../../core/trade.api';
import { Item, Master } from '../../core/trade.models';
import { IMPORTS, openImport } from '../../shared/import-dialog';
import { SHARED } from '../../shared/shared';

/** Create or edit an item. Closes with the saved item. */
@Component({
  selector: 'app-item-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ (data.item ? 'item.edit' : 'item.new') | t }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field>
          <mat-label>{{ 'item.sku' | t }}</mat-label>
          <input matInput formControlName="sku" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'item.type' | t }}</mat-label>
          <mat-select formControlName="itemType">
            <mat-option value="Stock">{{ 'item.type.Stock' | t }}</mat-option>
            <mat-option value="Service">{{ 'item.type.Service' | t }}</mat-option>
          </mat-select>
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
          <mat-label>{{ 'item.group' | t }}</mat-label>
          <mat-select formControlName="itemGroupId">
            @for (g of groups(); track g.id) { <mat-option [value]="g.id">{{ g | bi }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'item.uom' | t }}</mat-label>
          <mat-select formControlName="uomId">
            @for (u of uoms(); track u.id) { <mat-option [value]="u.id"><span class="ltr">{{ u.code }}</span> · {{ u | bi }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'item.barcode' | t }}</mat-label>
          <input matInput formControlName="barcode" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'item.reorderLevel' | t }}</mat-label>
          <input matInput type="number" min="0" formControlName="reorderLevel" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'item.reorderQuantity' | t }}</mat-label>
          <input matInput type="number" min="0" formControlName="reorderQuantity" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'item.purchasePrice' | t }}</mat-label>
          <input matInput type="number" min="0" formControlName="purchasePrice" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'item.salesPrice' | t }}</mat-label>
          <input matInput type="number" min="0" formControlName="salesPrice" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'item.hsCode' | t }}</mat-label>
          <input matInput formControlName="hsCode" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'item.brand' | t }}</mat-label>
          <input matInput formControlName="brand" dir="ltr" maxlength="80" />
        </mat-form-field>
        @if (form.value.itemType === 'Stock') {
          <mat-slide-toggle formControlName="trackBatches">{{ 'item.trackBatches' | t }}</mat-slide-toggle>
          <mat-slide-toggle formControlName="trackSerials">{{ 'item.trackSerials' | t }}</mat-slide-toggle>
        }
        <mat-slide-toggle formControlName="isActive">{{ 'item.active' | t }}</mat-slide-toggle>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid || busy()" (click)="save()">{{ 'app.save' | t }}</button>
    </mat-dialog-actions>
  `
})
export class ItemDialog {
  protected readonly data = inject<{ item?: Item }>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<ItemDialog, Item>);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  protected readonly groups = signal<Master[]>([]);
  protected readonly uoms = signal<Master[]>([]);
  protected readonly busy = signal(false);
  protected readonly form = inject(FormBuilder).group({
    sku: [this.data.item?.sku ?? '', Validators.required],
    itemType: [this.data.item?.itemType ?? 'Stock', Validators.required],
    nameEn: [this.data.item?.nameEn ?? ''],
    nameAr: [this.data.item?.nameAr ?? ''],
    itemGroupId: [this.data.item?.itemGroupId ?? '', Validators.required],
    uomId: [this.data.item?.uomId ?? '', Validators.required],
    barcode: [this.data.item?.barcode ?? ''],
    reorderLevel: [this.data.item?.reorderLevel ?? (null as number | null)],
    reorderQuantity: [this.data.item?.reorderQuantity ?? (null as number | null)],
    purchasePrice: [this.data.item?.purchasePrice ?? (null as number | null)],
    salesPrice: [this.data.item?.salesPrice ?? (null as number | null)],
    hsCode: [this.data.item?.hsCode ?? ''],
    brand: [this.data.item?.brand ?? ''],
    isActive: [this.data.item?.isActive ?? true],
    trackBatches: [this.data.item?.trackBatches ?? false],
    trackSerials: [this.data.item?.trackSerials ?? false]
  });

  constructor() {
    this.trade.groups().subscribe((g) => {
      this.groups.set(g.filter((x) => x.isActive || x.id === this.data.item?.itemGroupId));
      if (!this.form.value.itemGroupId && g.length) this.form.patchValue({ itemGroupId: g[0].id });
    });
    this.trade.uoms().subscribe((u) => {
      this.uoms.set(u);
      if (!this.form.value.uomId) this.form.patchValue({ uomId: u.find((x) => x.code === 'PCS')?.id ?? u[0]?.id ?? '' });
    });
  }

  protected save(): void {
    const v = this.form.getRawValue();
    const num = (x: number | null | undefined) => (x === null || x === undefined || (x as unknown) === '' ? null : Number(x));
    this.busy.set(true);
    this.trade
      .saveItem(this.data.item?.id ?? null, {
        ...v,
        reorderLevel: num(v.reorderLevel),
        reorderQuantity: num(v.reorderQuantity),
        purchasePrice: num(v.purchasePrice),
        salesPrice: num(v.salesPrice)
      })
      .subscribe({
        next: (item) => {
          this.notify.success();
          this.ref.close(item);
        },
        error: (e) => {
          this.busy.set(false);
          this.notify.error(e);
        }
      });
  }
}

@Component({
  selector: 'app-items',
  imports: [...SHARED, MatPaginatorModule],
  template: `
    <div class="page-header">
      <h1>{{ 'item.title' | t }}</h1>
      <button mat-stroked-button (click)="exportCsv()"><mat-icon>download</mat-icon> {{ 'export.csv' | t }}</button>
      @if (auth.can('inv.items:manage')) {
        <button mat-stroked-button (click)="importExcel()"><mat-icon>upload_file</mat-icon> {{ 'xl.button' | t }}</button>
        <button mat-flat-button (click)="create()"><mat-icon>add</mat-icon> {{ 'item.new' | t }}</button>
      }
    </div>
    <div class="toolbar-row">
      <mat-form-field>
        <mat-label>{{ 'app.search' | t }}</mat-label>
        <input matInput [formControl]="search" />
        <mat-icon matSuffix>search</mat-icon>
      </mat-form-field>
      <mat-form-field>
        <mat-label>{{ 'item.type' | t }}</mat-label>
        <mat-select [formControl]="type">
          <mat-option value="">{{ 'item.allTypes' | t }}</mat-option>
          <mat-option value="Stock">{{ 'item.type.Stock' | t }}</mat-option>
          <mat-option value="Service">{{ 'item.type.Service' | t }}</mat-option>
        </mat-select>
      </mat-form-field>
    </div>
    @if (loading()) { <mat-progress-bar mode="indeterminate" /> }
    <div class="table-wrap">
      <table mat-table [dataSource]="rows()">
        <ng-container matColumnDef="sku">
          <th mat-header-cell *matHeaderCellDef>{{ 'item.sku' | t }}</th>
          <td mat-cell *matCellDef="let i"><span class="ltr">{{ i.sku }}</span></td>
        </ng-container>
        <ng-container matColumnDef="name">
          <th mat-header-cell *matHeaderCellDef>{{ 'item.name' | t }}</th>
          <td mat-cell *matCellDef="let i">
            {{ i | bi }} @if (i.brand) { <span class="chip ltr">{{ i.brand }}</span> }
            @if (!i.isActive) { <span class="chip">{{ 'app.no' | t }} · {{ 'item.active' | t }}</span> }
          </td>
        </ng-container>
        <ng-container matColumnDef="type">
          <th mat-header-cell *matHeaderCellDef>{{ 'item.type' | t }}</th>
          <td mat-cell *matCellDef="let i"><span class="chip" [class.info]="i.itemType === 'Service'">{{ 'item.type.' + i.itemType | t }}</span></td>
        </ng-container>
        <ng-container matColumnDef="onHand">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'item.onHand' | t }}</th>
          <td mat-cell *matCellDef="let i" class="num ltr">@if (i.itemType === 'Stock') { {{ i.quantityOnHand }} <span class="secondary">{{ i.uomCode }}</span> }</td>
        </ng-container>
        <ng-container matColumnDef="avg">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'item.avgCost' | t }}</th>
          <td mat-cell *matCellDef="let i" class="num ltr">@if (i.itemType === 'Stock') { {{ i.averageCost | money }} }</td>
        </ng-container>
        <ng-container matColumnDef="value">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'item.value' | t }}</th>
          <td mat-cell *matCellDef="let i" class="num ltr">@if (i.itemType === 'Stock') { {{ i.stockValue | money }} }</td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns" class="clickable" (click)="router.navigate(['/inventory/items', row.id])"></tr>
      </table>
      @if (!loading() && rows().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
      <mat-paginator [length]="total()" [pageSize]="pageSize" [pageSizeOptions]="[25, 50, 100]" (page)="page($event)" />
    </div>
  `
})
export class ItemsPage {
  protected readonly auth = inject(AuthService);
  protected readonly router = inject(Router);
  private readonly trade = inject(TradeApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);

  protected readonly columns = ['sku', 'name', 'type', 'onHand', 'avg', 'value'];
  protected readonly search = new FormControl('', { nonNullable: true });
  protected readonly type = new FormControl('', { nonNullable: true });
  protected readonly rows = signal<Item[]>([]);

  protected exportCsv(): void {
    downloadCsv('items.csv', ['SKU', 'Name', 'الاسم', 'Type', 'Unit', 'Barcode', 'On hand', 'Average cost', 'Value', 'Sales price'], this.rows().map((x) => [x.sku, x.nameEn, x.nameAr, x.itemType, x.uomCode, x.barcode ?? '', x.quantityOnHand, x.averageCost, x.stockValue, x.salesPrice ?? '']));
  }
  protected readonly total = signal(0);
  protected readonly loading = signal(false);
  protected pageSize = 50;
  private pageIndex = 0;

  constructor() {
    merge(this.search.valueChanges.pipe(debounceTime(300), distinctUntilChanged()), this.type.valueChanges)
      .pipe(takeUntilDestroyed())
      .subscribe(() => {
        this.pageIndex = 0;
        this.load();
      });
    this.load();
  }

  protected page(e: PageEvent): void {
    this.pageIndex = e.pageIndex;
    this.pageSize = e.pageSize;
    this.load();
  }

  protected create(): void {
    this.dialog
      .open(ItemDialog, { data: {} })
      .afterClosed()
      .subscribe((item?: Item) => item && void this.router.navigate(['/inventory/items', item.id]));
  }

  protected importExcel(): void {
    openImport(this.dialog, IMPORTS.items).subscribe((done) => done && this.load());
  }

  private load(): void {
    this.loading.set(true);
    this.trade
      .items({ search: this.search.value, type: this.type.value, page: this.pageIndex + 1, pageSize: this.pageSize })
      .subscribe({
        next: (r) => {
          this.rows.set(r.items);
          this.total.set(r.total);
          this.loading.set(false);
        },
        error: (e) => {
          this.loading.set(false);
          this.notify.error(e);
        }
      });
  }
}
