import { downloadCsv } from '../../shared/export';
import { Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { debounceTime, distinctUntilChanged } from 'rxjs';
import { AuthService } from '../../core/auth.service';
import { NotifyService } from '../../core/notify.service';
import { TradeApi } from '../../core/trade.api';
import { CURRENCIES, Supplier, WHT_CATEGORIES, WhtCategory } from '../../core/trade.models';
import { NATIONALITIES, SHARED } from '../../shared/shared';

@Component({
  selector: 'app-supplier-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ (data.supplier ? 'sup.edit' : 'sup.new') | t }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field>
          <mat-label>{{ 'sup.code' | t }}</mat-label>
          <input matInput formControlName="code" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'sup.country' | t }}</mat-label>
          <mat-select formControlName="country">
            @for (c of countries; track c.code) { <mat-option [value]="c.code">{{ c.code }} · {{ c.ar }} / {{ c.en }}</mat-option> }
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
        <mat-slide-toggle formControlName="isResident">{{ 'sup.resident' | t }}</mat-slide-toggle>
        @if (!form.controls.isResident.value) {
          <mat-slide-toggle formControlName="hasPermanentEstablishment">{{ 'sup.pe' | t }}</mat-slide-toggle>
        }
        @if (!form.controls.isResident.value && !form.controls.hasPermanentEstablishment.value) {
          <mat-form-field class="full">
            <mat-label>{{ 'sup.defaultWht' | t }}</mat-label>
            <mat-select formControlName="defaultWhtCategory">
              @for (w of whtCategories; track w) { <mat-option [value]="w">{{ 'wht.' + w | t }}</mat-option> }
            </mat-select>
            <mat-hint>{{ 'pinv.whtHint' | t }}</mat-hint>
          </mat-form-field>
        }
        <mat-form-field>
          <mat-label>{{ 'fx.currency' | t }}</mat-label>
          <mat-select formControlName="currency">
            @for (c of currencies; track c) { <mat-option [value]="c">{{ c }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'sup.terms' | t }}</mat-label>
          <input matInput type="number" min="0" max="365" formControlName="paymentTermsDays" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'sup.taxCard' | t }}</mat-label>
          <input matInput formControlName="taxCardNo" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'sup.cr' | t }}</mat-label>
          <input matInput formControlName="crNo" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'sup.email' | t }}</mat-label>
          <input matInput type="email" formControlName="email" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'sup.phone' | t }}</mat-label>
          <input matInput formControlName="phone" dir="ltr" />
        </mat-form-field>
        <mat-slide-toggle formControlName="isActive">{{ 'item.active' | t }}</mat-slide-toggle>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid || busy()" (click)="save()">{{ 'app.save' | t }}</button>
    </mat-dialog-actions>
  `
})
export class SupplierDialog {
  protected readonly data = inject<{ supplier?: Supplier }>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<SupplierDialog, Supplier>);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  protected readonly countries = NATIONALITIES;
  protected readonly whtCategories = WHT_CATEGORIES;
  protected readonly currencies = CURRENCIES;
  protected readonly busy = signal(false);
  private readonly s = this.data.supplier;
  protected readonly form = inject(FormBuilder).nonNullable.group({
    code: [this.s?.code ?? '', Validators.required],
    nameEn: [this.s?.nameEn ?? ''],
    nameAr: [this.s?.nameAr ?? ''],
    country: [this.s?.country ?? 'QA'],
    isResident: [this.s?.isResident ?? true],
    hasPermanentEstablishment: [this.s?.hasPermanentEstablishment ?? false],
    defaultWhtCategory: [(this.s?.defaultWhtCategory ?? 'None') as WhtCategory],
    paymentTermsDays: [this.s?.paymentTermsDays ?? 30, [Validators.min(0), Validators.max(365)]],
    currency: [this.s?.currency ?? 'QAR'],
    taxCardNo: [this.s?.taxCardNo ?? ''],
    crNo: [this.s?.crNo ?? ''],
    email: [this.s?.email ?? '', Validators.email],
    phone: [this.s?.phone ?? ''],
    isActive: [this.s?.isActive ?? true]
  });

  protected save(): void {
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.trade
      .saveSupplier(this.s?.id ?? null, {
        ...v,
        paymentTermsDays: Number(v.paymentTermsDays),
        defaultWhtCategory: v.isResident || v.hasPermanentEstablishment ? 'None' : v.defaultWhtCategory
      })
      .subscribe({
        next: (s) => {
          this.notify.success();
          this.ref.close(s);
        },
        error: (e) => {
          this.busy.set(false);
          this.notify.error(e);
        }
      });
  }
}

@Component({
  selector: 'app-suppliers',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'sup.title' | t }}</h1>
      <button mat-stroked-button (click)="exportCsv()"><mat-icon>download</mat-icon> {{ 'export.csv' | t }}</button>
      @if (auth.can('pur.suppliers:manage')) {
        <button mat-flat-button (click)="edit()"><mat-icon>add</mat-icon> {{ 'sup.new' | t }}</button>
      }
    </div>
    <div class="toolbar-row">
      <mat-form-field>
        <mat-label>{{ 'app.search' | t }}</mat-label>
        <input matInput [formControl]="search" />
        <mat-icon matSuffix>search</mat-icon>
      </mat-form-field>
    </div>
    <div class="table-wrap">
      <table mat-table [dataSource]="rows()">
        <ng-container matColumnDef="code">
          <th mat-header-cell *matHeaderCellDef>{{ 'sup.code' | t }}</th>
          <td mat-cell *matCellDef="let s"><span class="ltr">{{ s.code }}</span></td>
        </ng-container>
        <ng-container matColumnDef="name">
          <th mat-header-cell *matHeaderCellDef>{{ 'sup.name' | t }}</th>
          <td mat-cell *matCellDef="let s">
            {{ s | bi }}
            @if (!s.isActive) { <span class="chip">{{ 'app.no' | t }} · {{ 'item.active' | t }}</span> }
          </td>
        </ng-container>
        <ng-container matColumnDef="country">
          <th mat-header-cell *matHeaderCellDef>{{ 'sup.country' | t }}</th>
          <td mat-cell *matCellDef="let s">
            <span class="ltr">{{ s.country }}</span>
            @if (s.subjectToWht) { <span class="chip warn">{{ 'sup.whtFlag' | t }}</span> }
            @if (s.currency !== 'QAR') { <span class="chip info">{{ s.currency }}</span> }
          </td>
        </ng-container>
        <ng-container matColumnDef="terms">
          <th mat-header-cell *matHeaderCellDef>{{ 'sup.terms' | t }}</th>
          <td mat-cell *matCellDef="let s" class="ltr">{{ s.paymentTermsDays }}</td>
        </ng-container>
        <ng-container matColumnDef="contact">
          <th mat-header-cell *matHeaderCellDef>{{ 'sup.email' | t }}</th>
          <td mat-cell *matCellDef="let s"><span class="ltr">{{ s.email }}</span> <span class="secondary ltr">{{ s.phone }}</span></td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns" class="clickable" (click)="edit(row)"></tr>
      </table>
      @if (rows().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `
})
export class SuppliersPage {
  protected readonly auth = inject(AuthService);
  private readonly trade = inject(TradeApi);
  private readonly dialog = inject(MatDialog);
  protected readonly columns = ['code', 'name', 'country', 'terms', 'contact'];
  protected readonly search = new FormControl('', { nonNullable: true });
  protected readonly rows = signal<Supplier[]>([]);

  protected exportCsv(): void {
    downloadCsv('suppliers.csv', ['Code', 'Name', 'الاسم', 'Country', 'Resident', 'Currency', 'Payment terms', 'Phone', 'Email'], this.rows().map((x) => [x.code, x.nameEn, x.nameAr, x.country, x.isResident, x.currency, x.paymentTermsDays, x.phone ?? '', x.email ?? '']));
  }

  constructor() {
    this.search.valueChanges.pipe(debounceTime(300), distinctUntilChanged(), takeUntilDestroyed()).subscribe(() => this.load());
    this.load();
  }

  protected edit(supplier?: Supplier): void {
    if (supplier && !this.auth.can('pur.suppliers:manage')) return;
    this.dialog
      .open(SupplierDialog, { data: { supplier } })
      .afterClosed()
      .subscribe((saved?: Supplier) => saved && this.load());
  }

  private load(): void {
    this.trade.suppliers(this.search.value).subscribe((r) => this.rows.set(r));
  }
}
