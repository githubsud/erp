import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { filter, switchMap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { todayIso } from '../../core/dates';
import { Account } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { ConfirmDialog, ReverseDialog, ReverseResult } from '../../shared/dialogs';
import { downloadCsv } from '../../shared/export';
import { SHARED } from '../../shared/shared';

interface Category {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  usefulLifeMonths: number;
  assetAccountId: string;
  accumulatedAccountId: string;
  expenseAccountId: string;
  isActive: boolean;
}

interface Asset {
  id: string;
  assetNo: string;
  nameEn: string;
  nameAr: string;
  categoryId: string;
  categoryCode: string;
  status: 'Draft' | 'Active' | 'FullyDepreciated' | 'Disposed';
  acquisitionDate: string;
  cost: number;
  salvageValue: number;
  usefulLifeMonths: number;
  monthlyDepreciation: number;
  accumulatedDepreciation: number;
  netBookValue: number;
  location: string | null;
  serialNo: string | null;
  disposalDate: string | null;
}

interface Register {
  assets: Asset[];
  cost: number;
  accumulated: number;
  netBookValue: number;
  ledgerCost: number;
  ledgerAccumulated: number;
}

interface Run {
  id: string;
  runNo: string;
  year: number;
  month: number;
  postingDate: string;
  total: number;
  status: string;
  assetCount: number;
}

@Component({
  selector: 'app-assets',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'fa.title' | t }}</h1>
      @if (canManage) {
        <button mat-flat-button (click)="newAsset()" [disabled]="!categories().length"><mat-icon>add</mat-icon> {{ 'fa.new' | t }}</button>
      }
    </div>
    <mat-tab-group mat-stretch-tabs="false">
      <mat-tab [label]="'fa.register' | t">
        @if (register(); as r) {
          <div class="reconcile" [class.bad]="r.cost !== r.ledgerCost || r.accumulated !== r.ledgerAccumulated">
            {{ 'fa.cost' | t }} <b class="ltr">{{ r.cost | money }}</b> · {{ 'fa.accumulated' | t }} <b class="ltr">{{ r.accumulated | money }}</b>
            · {{ 'fa.nbv' | t }} <b class="ltr">{{ r.netBookValue | money }}</b>
            <span class="secondary">({{ 'fa.ledger' | t }} <span class="ltr">{{ r.ledgerCost | money }} / {{ r.ledgerAccumulated | money }}</span>)</span>
            <span class="spacer"></span>
            <button mat-button (click)="exportCsv()"><mat-icon>download</mat-icon> {{ 'export.csv' | t }}</button>
          </div>
          <table class="line-editor">
            <thead><tr><th>{{ 'fa.no' | t }}</th><th>{{ 'item.name' | t }}</th><th>{{ 'fa.category' | t }}</th><th>{{ 'fa.acquired' | t }}</th>
              <th class="num">{{ 'fa.cost' | t }}</th><th class="num">{{ 'fa.monthly' | t }}</th><th class="num">{{ 'fa.accumulated' | t }}</th>
              <th class="num">{{ 'fa.nbv' | t }}</th><th>{{ 'app.status' | t }}</th><th></th></tr></thead>
            <tbody>
              @for (a of r.assets; track a.id) {
                <tr>
                  <td class="ltr">{{ a.assetNo }}</td>
                  <td>{{ a | bi }}@if (a.serialNo || a.location) { <div class="secondary">{{ a.serialNo }} {{ a.location }}</div> }</td>
                  <td class="ltr">{{ a.categoryCode }}</td>
                  <td class="ltr">{{ a.acquisitionDate | d }}</td>
                  <td class="num ltr">{{ a.cost | money }}</td>
                  <td class="num ltr">{{ a.monthlyDepreciation | money }}</td>
                  <td class="num ltr">{{ a.accumulatedDepreciation | money }}</td>
                  <td class="num ltr"><b>{{ a.netBookValue | money }}</b></td>
                  <td><span class="chip" [class.ok]="a.status === 'Active'" [class.warn]="a.status === 'Draft'" [class.info]="a.status === 'FullyDepreciated'">{{ 'fa.status.' + a.status | t }}</span></td>
                  <td class="actions-cell">
                    @if (canManage && a.status === 'Draft') { <button mat-stroked-button (click)="capitalize(a)">{{ 'fa.capitalize' | t }}</button> }
                    @if (canManage && (a.status === 'Active' || a.status === 'FullyDepreciated')) { <button mat-button (click)="dispose(a)">{{ 'fa.dispose' | t }}</button> }
                  </td>
                </tr>
              }
            </tbody>
          </table>
          @if (!r.assets.length) { <p class="secondary">{{ 'app.none' | t }}</p> }
        }
      </mat-tab>
      <mat-tab [label]="'fa.depreciation' | t">
        <div class="tab-body">
          @if (canManage) {
            <div class="toolbar-row">
              <mat-form-field><mat-label>{{ 'payroll.month' | t }}</mat-label><input matInput type="month" [formControl]="month" /></mat-form-field>
              <button mat-flat-button (click)="runDepreciation()">{{ 'fa.runDepreciation' | t }}</button>
            </div>
          }
          <table class="line-editor">
            <thead><tr><th>{{ 'fa.runNo' | t }}</th><th>{{ 'payroll.month' | t }}</th><th class="num">{{ 'fa.assets' | t }}</th><th class="num">{{ 'app.amount' | t }}</th>
              <th>{{ 'app.status' | t }}</th><th></th></tr></thead>
            <tbody>
              @for (r of runs(); track r.id) {
                <tr>
                  <td class="ltr">{{ r.runNo }}</td><td>{{ 'month.' + r.month | t }} {{ r.year }}</td><td class="num ltr">{{ r.assetCount }}</td>
                  <td class="num ltr">{{ r.total | money }}</td><td>{{ 'doc.status.' + r.status | t }}</td>
                  <td>@if (canManage && r.status === 'Posted') { <button mat-icon-button (click)="reverseRun(r)"><mat-icon>undo</mat-icon></button> }</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      </mat-tab>
      <mat-tab [label]="'fa.categories' | t">
        <div class="tab-body">
          @if (canManage) { <button mat-stroked-button (click)="editCategory(null)"><mat-icon>add</mat-icon> {{ 'fa.newCategory' | t }}</button> }
          <table class="line-editor">
            <thead><tr><th>{{ 'fa.code' | t }}</th><th>{{ 'item.name' | t }}</th><th class="num">{{ 'fa.life' | t }}</th><th>{{ 'fa.accounts' | t }}</th><th></th></tr></thead>
            <tbody>
              @for (c of categories(); track c.id) {
                <tr>
                  <td class="ltr">{{ c.code }}</td><td>{{ c | bi }}</td><td class="num ltr">{{ c.usefulLifeMonths }}</td>
                  <td class="ltr secondary">{{ code(c.assetAccountId) }} / {{ code(c.accumulatedAccountId) }} / {{ code(c.expenseAccountId) }}</td>
                  <td>@if (canManage) { <button mat-icon-button (click)="editCategory(c)"><mat-icon>edit</mat-icon></button> }</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      </mat-tab>
    </mat-tab-group>
  `,
  styles: `
    .tab-body { margin-block-start: 16px; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 12px; }
    .reconcile { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; padding: 10px 16px; border-radius: 12px; background: #d8f3e5; color: #0b5a35; margin: 16px 0 12px; }
    .reconcile.bad { background: #ffdcd9; color: #8c1d18; }
    .spacer { flex: 1; }
    .actions-cell { white-space: nowrap; text-align: end; }
  `
})
export class AssetsPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  protected readonly canManage = inject(AuthService).can('fin.assets:manage');
  protected readonly register = signal<Register | null>(null);
  protected readonly categories = signal<Category[]>([]);
  protected readonly runs = signal<Run[]>([]);
  protected readonly accounts = signal<Account[]>([]);
  protected readonly month = inject(FormBuilder).nonNullable.control(todayIso().slice(0, 7));
  private readonly accountCodes = computed(() => new Map(this.accounts().map((a) => [a.id, a.code])));

  ngOnInit(): void {
    this.api.accounts().subscribe((a) => this.accounts.set(a));
    this.load();
  }

  private load(): void {
    this.api.get<Register>('finance/assets').subscribe({ next: (r) => this.register.set(r), error: (e) => this.notify.error(e) });
    this.api.get<Category[]>('finance/assets/categories').subscribe((c) => this.categories.set(c));
    this.api.get<Run[]>('finance/assets/depreciation-runs').subscribe((r) => this.runs.set(r));
  }

  protected code(id: string): string {
    return this.accountCodes().get(id) ?? '';
  }

  protected newAsset(): void {
    this.dialog.open(AssetDialog, { data: { categories: this.categories() }, width: '640px' }).afterClosed().pipe(filter(Boolean)).subscribe(() => this.load());
  }

  protected editCategory(c: Category | null): void {
    this.dialog.open(CategoryDialog, { data: { category: c, accounts: this.accounts() }, width: '560px' }).afterClosed().pipe(filter(Boolean)).subscribe(() => this.load());
  }

  protected capitalize(a: Asset): void {
    this.dialog
      .open(CapitalizeDialog, { data: { asset: a, accounts: this.accounts() }, width: '460px' })
      .afterClosed()
      .pipe(filter(Boolean), switchMap((body) => this.api.post(`finance/assets/${a.id}/capitalize`, body)))
      .subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  protected dispose(a: Asset): void {
    this.dialog
      .open(DisposeDialog, { data: { asset: a, accounts: this.accounts() }, width: '460px' })
      .afterClosed()
      .pipe(filter(Boolean), switchMap((body) => this.api.post(`finance/assets/${a.id}/dispose`, body)))
      .subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  protected runDepreciation(): void {
    const [year, month] = this.month.value.split('-').map(Number);
    this.dialog
      .open(ConfirmDialog, { data: { title: 'fa.runDepreciation', message: 'fa.runConfirm' } })
      .afterClosed()
      .pipe(filter(Boolean), switchMap(() => this.api.post('finance/assets/depreciation-runs', { year, month })))
      .subscribe({
        next: () => {
          this.notify.success();
          this.load();
        },
        error: (e) => this.notify.error(e)
      });
  }

  protected reverseRun(r: Run): void {
    this.dialog
      .open(ReverseDialog, { data: {} })
      .afterClosed()
      .pipe(
        filter((x): x is ReverseResult => !!x),
        switchMap((x) => this.api.post(`finance/assets/depreciation-runs/${r.id}/reverse`, { date: x.date, reason: x.reason }))
      )
      .subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  protected exportCsv(): void {
    downloadCsv('fixed_assets.csv', ['Asset', 'Name', 'الاسم', 'Category', 'Acquired', 'Cost', 'Monthly', 'Accumulated', 'NBV', 'Status'],
      (this.register()?.assets ?? []).map((a) => [a.assetNo, a.nameEn, a.nameAr, a.categoryCode, a.acquisitionDate, a.cost, a.monthlyDepreciation,
        a.accumulatedDepreciation, a.netBookValue, a.status]));
  }
}

@Component({
  selector: 'app-asset-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'fa.new' | t }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field><mat-label>{{ 'sup.nameAr' | t }}</mat-label><input matInput formControlName="nameAr" dir="rtl" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'sup.nameEn' | t }}</mat-label><input matInput formControlName="nameEn" dir="ltr" /></mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'fa.category' | t }}</mat-label>
          <mat-select formControlName="categoryId">
            @for (c of data.categories; track c.id) { <mat-option [value]="c.id">{{ c.code }} · {{ c | bi }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field><mat-label>{{ 'fa.acquired' | t }}</mat-label><input matInput type="date" formControlName="acquisitionDate" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'fa.cost' | t }}</mat-label><input matInput type="number" min="0" formControlName="cost" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'fa.salvage' | t }}</mat-label><input matInput type="number" min="0" formControlName="salvageValue" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'fa.life' | t }}</mat-label><input matInput type="number" min="1" formControlName="usefulLifeMonths" dir="ltr" />
          <mat-hint>{{ 'fa.lifeHint' | t }}</mat-hint></mat-form-field>
        <mat-form-field><mat-label>{{ 'fa.opening' | t }}</mat-label><input matInput type="number" min="0" formControlName="openingDepreciation" dir="ltr" />
          <mat-hint>{{ 'fa.openingHint' | t }}</mat-hint></mat-form-field>
        <mat-form-field><mat-label>{{ 'fa.serial' | t }}</mat-label><input matInput formControlName="serialNo" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'fa.location' | t }}</mat-label><input matInput formControlName="location" /></mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid" (click)="save()">{{ 'app.save' | t }}</button>
    </mat-dialog-actions>
  `
})
export class AssetDialog {
  protected readonly data = inject<{ categories: Category[] }>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<AssetDialog, boolean>);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    nameEn: [''], nameAr: [''], categoryId: [this.data.categories[0]?.id ?? '', Validators.required], acquisitionDate: [todayIso(), Validators.required],
    cost: [0, [Validators.required, Validators.min(0.01)]], salvageValue: [0], usefulLifeMonths: [null as number | null], openingDepreciation: [0],
    serialNo: [''], location: ['']
  });

  protected save(): void {
    const v = this.form.getRawValue();
    this.api.post('finance/assets', { ...v, cost: Number(v.cost), salvageValue: Number(v.salvageValue) || 0, openingDepreciation: Number(v.openingDepreciation) || 0,
      usefulLifeMonths: v.usefulLifeMonths ? Number(v.usefulLifeMonths) : null }).subscribe({
      next: () => this.ref.close(true),
      error: (e) => this.notify.error(e)
    });
  }
}

@Component({
  selector: 'app-category-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ (data.category ? 'app.edit' : 'fa.newCategory') | t }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field><mat-label>{{ 'fa.code' | t }}</mat-label><input matInput formControlName="code" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'fa.life' | t }}</mat-label><input matInput type="number" min="1" formControlName="usefulLifeMonths" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'sup.nameAr' | t }}</mat-label><input matInput formControlName="nameAr" dir="rtl" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'sup.nameEn' | t }}</mat-label><input matInput formControlName="nameEn" dir="ltr" /></mat-form-field>
        @for (f of accountFields; track f.key) {
          <mat-form-field class="wide">
            <mat-label>{{ f.label | t }}</mat-label>
            <mat-select [formControlName]="f.key">
              @for (a of postable; track a.id) { <mat-option [value]="a.id"><span class="ltr">{{ a.code }}</span> · {{ a | bi }}</mat-option> }
            </mat-select>
          </mat-form-field>
        }
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid" (click)="save()">{{ 'app.save' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `.wide { grid-column: 1 / -1; }`
})
export class CategoryDialog {
  protected readonly data = inject<{ category: Category | null; accounts: Account[] }>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<CategoryDialog, boolean>);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly postable = this.data.accounts.filter((a) => a.isPostable);
  private readonly byCode = (code: string) => this.postable.find((a) => a.code === code)?.id ?? '';
  protected readonly accountFields = [
    { key: 'assetAccountId', label: 'fa.assetAccount' }, { key: 'accumulatedAccountId', label: 'fa.accumulatedAccount' },
    { key: 'expenseAccountId', label: 'fa.expenseAccount' }
  ] as const;
  protected readonly form = inject(FormBuilder).nonNullable.group({
    code: [this.data.category?.code ?? '', Validators.required], nameEn: [this.data.category?.nameEn ?? ''], nameAr: [this.data.category?.nameAr ?? ''],
    usefulLifeMonths: [this.data.category?.usefulLifeMonths ?? 60, [Validators.required, Validators.min(1)]],
    assetAccountId: [this.data.category?.assetAccountId ?? this.byCode('1210'), Validators.required],
    accumulatedAccountId: [this.data.category?.accumulatedAccountId ?? this.byCode('1220'), Validators.required],
    expenseAccountId: [this.data.category?.expenseAccountId ?? this.byCode('5350'), Validators.required]
  });

  protected save(): void {
    const v = this.form.getRawValue();
    const body = { ...v, usefulLifeMonths: Number(v.usefulLifeMonths) };
    (this.data.category ? this.api.put(`finance/assets/categories/${this.data.category.id}`, body) : this.api.post('finance/assets/categories', body))
      .subscribe({ next: () => this.ref.close(true), error: (e) => this.notify.error(e) });
  }
}

@Component({
  selector: 'app-capitalize-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'fa.capitalize' | t }} · <span class="ltr">{{ data.asset.assetNo }}</span></h2>
    <mat-dialog-content>
      <p class="secondary">{{ 'fa.capitalizeHint' | t }}</p>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field><mat-label>{{ 'app.date' | t }}</mat-label><input matInput type="date" formControlName="date" /></mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'fa.paidFrom' | t }}</mat-label>
          <mat-select formControlName="offsetAccountId">
            @for (a of offsets; track a.id) { <mat-option [value]="a.id"><span class="ltr">{{ a.code }}</span> · {{ a | bi }}</mat-option> }
          </mat-select>
        </mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid" (click)="ref.close(form.getRawValue())">{{ 'doc.post' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `.secondary { color: var(--mat-sys-on-surface-variant); }`
})
export class CapitalizeDialog {
  protected readonly data = inject<{ asset: Asset; accounts: Account[] }>(MAT_DIALOG_DATA);
  protected readonly ref = inject(MatDialogRef<CapitalizeDialog>);
  protected readonly offsets = this.data.accounts.filter((a) => a.isPostable && !a.isControl && (a.subtype === 'bank' || a.subtype === 'cash'
    || ['2130', '3150'].includes(a.code)));
  protected readonly form = inject(FormBuilder).nonNullable.group({
    date: [this.data.asset.acquisitionDate, Validators.required],
    offsetAccountId: [this.offsets.find((a) => a.subtype === 'bank')?.id ?? '', Validators.required]
  });
}

@Component({
  selector: 'app-dispose-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'fa.dispose' | t }} · <span class="ltr">{{ data.asset.assetNo }}</span></h2>
    <mat-dialog-content>
      <p class="secondary">{{ 'fa.nbv' | t }}: <b class="ltr">{{ data.asset.netBookValue | money }}</b></p>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field><mat-label>{{ 'app.date' | t }}</mat-label><input matInput type="date" formControlName="date" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'fa.proceeds' | t }}</mat-label><input matInput type="number" min="0" formControlName="proceeds" dir="ltr" /></mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'fa.receivedIn' | t }}</mat-label>
          <mat-select formControlName="proceedsAccountId">
            @for (a of banks; track a.id) { <mat-option [value]="a.id"><span class="ltr">{{ a.code }}</span> · {{ a | bi }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field><mat-label>{{ 'app.reason' | t }}</mat-label><input matInput formControlName="reason" /></mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid" (click)="close()">{{ 'doc.post' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `.secondary { color: var(--mat-sys-on-surface-variant); }`
})
export class DisposeDialog {
  protected readonly data = inject<{ asset: Asset; accounts: Account[] }>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<DisposeDialog>);
  protected readonly banks = this.data.accounts.filter((a) => a.isPostable && (a.subtype === 'bank' || a.subtype === 'cash'));
  protected readonly form = inject(FormBuilder).nonNullable.group({
    date: [todayIso(), Validators.required], proceeds: [0, Validators.min(0)], proceedsAccountId: [this.banks.find((a) => a.subtype === 'bank')?.id ?? ''],
    reason: ['']
  });

  protected close(): void {
    const v = this.form.getRawValue();
    this.ref.close({ date: v.date, proceeds: Number(v.proceeds) || 0, proceedsAccountId: Number(v.proceeds) > 0 ? v.proceedsAccountId : null, reason: v.reason || null });
  }
}
