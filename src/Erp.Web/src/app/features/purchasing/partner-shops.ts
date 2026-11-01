import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { Router } from '@angular/router';
import { debounceTime, filter, switchMap } from 'rxjs';
import { AuthService } from '../../core/auth.service';
import {
  CommissionRow, CommissionType, ConsignedItem, ConsignmentApi, Movement, MovementType, PartnerShop, Settlement, Statement
} from '../../core/consignment.api';
import { addDaysIso, monthStartIso, todayIso } from '../../core/dates';
import { NotifyService } from '../../core/notify.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { TradeApi } from '../../core/trade.api';
import { Item, Master, Supplier } from '../../core/trade.models';
import { ConfirmDialog, ReverseDialog, ReverseResult } from '../../shared/dialogs';
import { SHARED } from '../../shared/shared';

/** "10%" or "15.00 / bottle" as the agreement reads. */
function commissionText(type: CommissionType, value: number, perBottle: string): string {
  return type === 'Percent' ? `${value}%` : `${value.toFixed(2)} ${perBottle}`;
}

/** The last day of the previous month: the usual end of a monthly statement. */
function lastMonthEnd(): string {
  const today = todayIso();
  return addDaysIso(monthStartIso(Number(today.slice(0, 4)), Number(today.slice(5, 7))), -1);
}

/** Partner shops whose goods we sell as their agent (goods on trust, بضاعة أمانة). */
@Component({
  selector: 'app-partner-shops',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'gt.title' | t }}</h1>
      <span class="spacer"></span>
      <a mat-stroked-button routerLink="/purchasing/partner-shops/commissions"><mat-icon>percent</mat-icon> {{ 'gt.commissions' | t }}</a>
      @if (canManage) { <button mat-flat-button (click)="create()"><mat-icon>add</mat-icon> {{ 'gt.newShop' | t }}</button> }
    </div>
    <p class="secondary">{{ 'gt.hint' | t }}</p>
    <div class="table-wrap">
      <table class="line-editor">
        <thead><tr><th>{{ 'gt.shop' | t }}</th><th>{{ 'gt.commission' | t }}</th><th class="num">{{ 'gt.perfumes' | t }}</th>
          <th class="num">{{ 'gt.withUs' | t }}</th><th class="num">{{ 'gt.owed' | t }}</th><th>{{ 'gt.settledTo' | t }}</th><th>{{ 'app.status' | t }}</th></tr></thead>
        <tbody>
          @for (s of shops(); track s.id) {
            <tr class="clickable" [routerLink]="['/purchasing/partner-shops', s.supplierId]">
              <td>{{ s | bi }} <span class="secondary ltr">{{ s.supplierCode }}</span></td>
              <td class="ltr">{{ commission(s) }}</td>
              <td class="num ltr">{{ s.itemCount }}</td>
              <td class="num ltr">{{ s.onHand }}</td>
              <td class="num ltr"><b>{{ s.owed | money }}</b></td>
              <td class="ltr">{{ s.settledTo | d }}</td>
              <td><span class="chip" [class.ok]="s.isActive">{{ (s.isActive ? 'gt.active' : 'gt.inactive') | t }}</span></td>
            </tr>
          }
        </tbody>
      </table>
    </div>
    @if (!shops().length) { <p class="secondary">{{ 'gt.noShops' | t }}</p> }
  `,
  styles: `.spacer { flex: 1; } .clickable { cursor: pointer; } .secondary { color: var(--mat-sys-on-surface-variant); font-size: 13px; }
    .page-header { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }`
})
export class PartnerShopsPage implements OnInit {
  private readonly api = inject(ConsignmentApi);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  private readonly notify = inject(NotifyService);
  private readonly i18n = inject(I18nService);
  protected readonly canManage = inject(AuthService).can('pur.consignment:manage');
  protected readonly shops = signal<PartnerShop[]>([]);

  ngOnInit(): void {
    this.api.shops().subscribe({ next: (s) => this.shops.set(s), error: (e) => this.notify.error(e) });
  }

  protected commission(s: PartnerShop): string {
    return commissionText(s.commissionType, s.commission, this.i18n.t('gt.perBottle'));
  }

  protected create(): void {
    this.dialog.open(ShopDialog, { data: null, width: '640px', maxWidth: '95vw' }).afterClosed().pipe(filter(Boolean))
      .subscribe((s: PartnerShop) => void this.router.navigate(['/purchasing/partner-shops', s.supplierId]));
  }
}

/** One partner shop: its perfumes, the bottles in and out, and its statements. */
@Component({
  selector: 'app-partner-shop',
  imports: [...SHARED],
  template: `
    @if (shop(); as s) {
      <div class="page-header">
        <a mat-icon-button routerLink="/purchasing/partner-shops" [attr.aria-label]="'app.back' | t"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
        <h1>{{ s | bi }}</h1>
        <span class="spacer"></span>
        @if (canManage) { <button mat-stroked-button (click)="edit(s)"><mat-icon>edit</mat-icon> {{ 'gt.agreement' | t }}</button> }
      </div>
      <div class="stats">
        <div class="stat"><span>{{ 'gt.commission' | t }}</span><b class="ltr">{{ commission(s.commissionType, s.commission) }}</b></div>
        <div class="stat"><span>{{ 'gt.perfumes' | t }}</span><b class="ltr">{{ s.itemCount }}</b></div>
        <div class="stat"><span>{{ 'gt.withUs' | t }}</span><b class="ltr">{{ s.onHand }}</b></div>
        <div class="stat"><span>{{ 'gt.owed' | t }}</span><b class="ltr">{{ s.owed | money }}</b></div>
        <div class="stat"><span>{{ 'gt.payoutDay' | t }}</span><b class="ltr">{{ s.payoutDay }}</b></div>
        @if (!s.isActive) { <div class="stat"><span>{{ 'app.status' | t }}</span><b>{{ 'gt.inactive' | t }}</b></div> }
      </div>
      <mat-tab-group mat-stretch-tabs="false">
        <mat-tab [label]="'gt.perfumes' | t">
          <div class="tab-body">
            <div class="actions">
              @if (canManage && s.isActive) { <button mat-flat-button (click)="addPerfumes(s)"><mat-icon>add</mat-icon> {{ 'gt.addPerfumes' | t }}</button> }
            </div>
            <div class="table-wrap">
              <table class="line-editor">
                <thead><tr><th>{{ 'item.sku' | t }}</th><th>{{ 'gt.perfume' | t }}</th><th class="num">{{ 'gt.shopPrice' | t }}</th>
                  <th>{{ 'gt.commission' | t }}</th><th class="num">{{ 'gt.withUs' | t }}</th><th></th></tr></thead>
                <tbody>
                  @for (i of items(); track i.itemId) {
                    <tr>
                      <td class="ltr">{{ i.sku }}</td><td>{{ i | bi }}</td><td class="num ltr">{{ i.salesPrice ?? 0 | money }}</td>
                      <td class="ltr">{{ commission(i.commissionType, i.commission) }}</td><td class="num ltr">{{ i.onHand }}</td>
                      <td>@if (canManage && i.onHand === 0) {
                        <button mat-icon-button (click)="release(i)" [attr.aria-label]="('gt.remove' | t) + ' ' + i.sku"><mat-icon>link_off</mat-icon></button> }</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            @if (!items().length) { <p class="secondary">{{ 'gt.noPerfumes' | t }}</p> }
            <p class="secondary">{{ 'gt.perfumesHint' | t }}</p>
          </div>
        </mat-tab>
        <mat-tab [label]="'gt.goods' | t">
          <div class="tab-body">
            <div class="actions">
              @if (canManage && items().length) {
                @if (s.isActive) { <button mat-flat-button (click)="move(s, 'Receipt')"><mat-icon>move_to_inbox</mat-icon> {{ 'gt.receive' | t }}</button> }
                <button mat-stroked-button (click)="move(s, 'Return')"><mat-icon>outbox</mat-icon> {{ 'gt.giveBack' | t }}</button>
              }
            </div>
            <div class="table-wrap">
              <table class="line-editor">
                <thead><tr><th>{{ 'gt.docNo' | t }}</th><th>{{ 'app.date' | t }}</th><th>{{ 'gt.kind' | t }}</th><th class="num">{{ 'gt.bottles' | t }}</th>
                  <th>{{ 'app.status' | t }}</th><th></th></tr></thead>
                <tbody>
                  @for (m of movements(); track m.id) {
                    <tr>
                      <td class="ltr">{{ m.movementNo }}</td><td class="ltr">{{ m.date | d }}</td>
                      <td><span class="chip" [class.ok]="m.movementType === 'Receipt'">{{ 'gt.kind.' + m.movementType | t }}</span></td>
                      <td class="num ltr">{{ bottles(m) }}</td>
                      <td><span class="chip" [class.bad]="m.status === 'Reversed'">{{ 'gt.status.' + m.status | t }}</span></td>
                      <td class="row-actions">
                        <a mat-icon-button [routerLink]="['/purchasing/partner-shops/receipts', m.id]" [attr.aria-label]="('gt.print' | t) + ' ' + m.movementNo"><mat-icon>print</mat-icon></a>
                        @if (canManage && m.status === 'Posted') {
                          <button mat-icon-button (click)="reverse(m)" [attr.aria-label]="('payroll.reverse' | t) + ' ' + m.movementNo"><mat-icon>undo</mat-icon></button> }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            @if (!movements().length) { <p class="secondary">{{ 'gt.noGoods' | t }}</p> }
          </div>
        </mat-tab>
        <mat-tab [label]="'gt.statements' | t">
          <div class="tab-body">
            <form class="actions" [formGroup]="statementForm" (ngSubmit)="openStatement()">
              <mat-form-field subscriptSizing="dynamic"><mat-label>{{ 'gt.periodEnd' | t }}</mat-label>
                <input matInput type="date" formControlName="periodEnd" /></mat-form-field>
              <button mat-flat-button type="submit" [disabled]="statementForm.invalid"><mat-icon>receipt_long</mat-icon> {{ 'gt.newStatement' | t }}</button>
            </form>
            <div class="table-wrap">
              <table class="line-editor">
                <thead><tr><th>{{ 'gt.docNo' | t }}</th><th>{{ 'gt.period' | t }}</th><th class="num">{{ 'gt.sales' | t }}</th>
                  <th class="num">{{ 'gt.commission' | t }}</th><th class="num">{{ 'gt.dueToShop' | t }}</th><th>{{ 'gt.invoice' | t }}</th></tr></thead>
                <tbody>
                  @for (x of settlements(); track x.id) {
                    <tr class="clickable" [routerLink]="['/purchasing/partner-shops/statements', x.id]">
                      <td class="ltr">{{ x.settlementNo }}</td><td class="ltr">{{ x.periodStart | d }} – {{ x.periodEnd | d }}</td>
                      <td class="num ltr">{{ x.sales | money }}</td><td class="num ltr">{{ x.commission | money }}</td><td class="num ltr"><b>{{ x.amount | money }}</b></td>
                      <td><span class="ltr">{{ x.invoiceNo }}</span>
                        <span class="chip" [class.ok]="x.invoiceStatus === 'Paid'" [class.bad]="x.invoiceStatus === 'Reversed'">{{ 'gt.inv.' + x.invoiceStatus | t }}</span></td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            @if (!settlements().length) { <p class="secondary">{{ 'gt.noStatements' | t }}</p> }
            <p class="secondary">{{ 'gt.statementsHint' | t }}</p>
          </div>
        </mat-tab>
      </mat-tab-group>
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: `
    .spacer { flex: 1; }
    .page-header { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
    .tab-body { margin-block-start: 16px; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 13px; }
    .clickable { cursor: pointer; }
    .stats { display: flex; gap: 12px; flex-wrap: wrap; margin: 12px 0; }
    .stat { display: flex; flex-direction: column; padding: 10px 16px; border-radius: 12px; background: var(--mat-sys-surface-container); min-width: 130px; }
    .stat b { color: var(--mat-sys-primary); }
    .actions { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; margin-bottom: 12px; }
    .row-actions { white-space: nowrap; }
  `
})
export class PartnerShopPage implements OnInit {
  readonly id = input.required<string>();
  private readonly api = inject(ConsignmentApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly router = inject(Router);
  private readonly i18n = inject(I18nService);
  protected readonly canManage = inject(AuthService).can('pur.consignment:manage');
  protected readonly shop = signal<PartnerShop | null>(null);
  protected readonly items = signal<ConsignedItem[]>([]);
  protected readonly movements = signal<Movement[]>([]);
  protected readonly settlements = signal<Settlement[]>([]);
  protected readonly statementForm = inject(FormBuilder).nonNullable.group({ periodEnd: [lastMonthEnd(), Validators.required] });

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    const id = this.id();
    this.api.shop(id).subscribe({ next: (s) => this.shop.set(s), error: (e) => this.notify.error(e) });
    this.api.items(id).subscribe((i) => this.items.set(i));
    this.api.movements(id).subscribe((m) => this.movements.set(m));
    this.api.settlements(id).subscribe((x) => this.settlements.set(x));
  }

  protected commission(type: CommissionType, value: number): string {
    return commissionText(type, value, this.i18n.t('gt.perBottle'));
  }

  protected bottles(m: Movement): number {
    return m.lines.reduce((sum, l) => sum + l.quantity, 0);
  }

  protected edit(s: PartnerShop): void {
    this.dialog.open(ShopDialog, { data: s, width: '640px', maxWidth: '95vw' }).afterClosed().pipe(filter(Boolean)).subscribe(() => this.load());
  }

  protected addPerfumes(s: PartnerShop): void {
    this.dialog.open(AddPerfumesDialog, { data: s, width: '720px', maxWidth: '95vw', autoFocus: false }).afterClosed().pipe(filter(Boolean))
      .subscribe(() => this.load());
  }

  protected release(i: ConsignedItem): void {
    this.dialog
      .open(ConfirmDialog, { data: { title: 'gt.remove', message: 'gt.removeConfirm' } })
      .afterClosed()
      .pipe(filter(Boolean), switchMap(() => this.api.releaseItems(this.id(), [i.itemId])))
      .subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  protected move(s: PartnerShop, type: MovementType): void {
    const data: MovementData = { shop: s, type, items: this.items() };
    this.dialog.open(MovementDialog, { data, width: '720px', maxWidth: '95vw', autoFocus: false }).afterClosed().pipe(filter(Boolean))
      .subscribe((m: Movement) => {
        this.notify.success();
        this.load();
        void this.router.navigate(['/purchasing/partner-shops/receipts', m.id]);
      });
  }

  protected reverse(m: Movement): void {
    this.dialog
      .open(ReverseDialog, { data: { date: todayIso() } })
      .afterClosed()
      .pipe(filter(Boolean), switchMap((r: ReverseResult) => this.api.reverseMovement(m.id, r.date, r.reason)))
      .subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  protected openStatement(): void {
    void this.router.navigate(['/purchasing/partner-shops', this.id(), 'statement'], { queryParams: { periodEnd: this.statementForm.getRawValue().periodEnd } });
  }
}

/** Starts or changes a partner shop's agreement. The shop must already be a supplier. */
@Component({
  selector: 'app-shop-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ (data ? 'gt.agreement' : 'gt.newShop') | t }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field class="wide"><mat-label>{{ 'gt.shop' | t }}</mat-label>
          <mat-select formControlName="supplierId">
            @for (s of suppliers(); track s.id) { <mat-option [value]="s.id">{{ s | bi }} <span class="ltr">({{ s.code }})</span></mat-option> }
          </mat-select>
          @if (!data) { <mat-hint>{{ 'gt.shopHint' | t }}</mat-hint> }
        </mat-form-field>
        <mat-form-field><mat-label>{{ 'gt.commissionType' | t }}</mat-label>
          <mat-select formControlName="commissionType">
            <mat-option value="Percent">{{ 'gt.type.Percent' | t }}</mat-option><mat-option value="Fixed">{{ 'gt.type.Fixed' | t }}</mat-option>
          </mat-select></mat-form-field>
        <mat-form-field><mat-label>{{ (form.controls.commissionType.value === 'Percent' ? 'gt.rate' : 'gt.amountPerBottle') | t }}</mat-label>
          <input matInput type="number" min="0" formControlName="commission" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'gt.startsOn' | t }}</mat-label><input matInput type="date" formControlName="startsOn" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'gt.endsOn' | t }}</mat-label><input matInput type="date" formControlName="endsOn" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'gt.payoutDay' | t }}</mat-label><input matInput type="number" min="1" max="28" formControlName="payoutDay" dir="ltr" />
          <mat-hint>{{ 'gt.payoutHint' | t }}</mat-hint></mat-form-field>
        <mat-form-field class="wide"><mat-label>{{ 'app.notes' | t }}</mat-label><textarea matInput rows="2" formControlName="notes"></textarea></mat-form-field>
        <mat-checkbox formControlName="isActive">{{ 'gt.active' | t }}</mat-checkbox>
        @if (data) { <mat-checkbox formControlName="applyToItems">{{ 'gt.applyToItems' | t }}</mat-checkbox> }
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid || saving()" (click)="save()">{{ 'app.save' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `.form-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 4px 12px; } .wide { grid-column: 1 / -1; }`
})
export class ShopDialog implements OnInit {
  protected readonly data = inject<PartnerShop | null>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<ShopDialog, PartnerShop>);
  private readonly api = inject(ConsignmentApi);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  protected readonly suppliers = signal<Supplier[]>([]);
  protected readonly saving = signal(false);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    supplierId: [{ value: this.data?.supplierId ?? '', disabled: !!this.data }, Validators.required],
    commissionType: [this.data?.commissionType ?? ('Percent' as CommissionType), Validators.required],
    commission: [this.data?.commission ?? 10, [Validators.required, Validators.min(0)]],
    startsOn: [this.data?.startsOn ?? todayIso(), Validators.required],
    endsOn: [this.data?.endsOn ?? ''],
    payoutDay: [this.data?.payoutDay ?? 5, [Validators.required, Validators.min(1), Validators.max(28)]],
    notes: [this.data?.notes ?? ''],
    isActive: [this.data?.isActive ?? true],
    applyToItems: [true]
  });

  ngOnInit(): void {
    this.trade.suppliers().subscribe((s) => this.suppliers.set(s.filter((x) => x.isActive || x.id === this.data?.supplierId)));
  }

  protected save(): void {
    const v = this.form.getRawValue();
    this.saving.set(true);
    this.api.saveShop({ ...v, endsOn: v.endsOn || null, notes: v.notes || null, commission: Number(v.commission), payoutDay: Number(v.payoutDay) }).subscribe({
      next: (s) => this.ref.close(s),
      error: (e) => {
        this.saving.set(false);
        this.notify.error(e);
      }
    });
  }
}

/** Puts inventory items under a partner shop: its own commission or the agreement's. */
@Component({
  selector: 'app-add-perfumes-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'gt.addPerfumes' | t }} · {{ data | bi }}</h2>
    <mat-dialog-content>
      <p class="secondary">{{ 'gt.addHint' | t }}</p>
      <mat-form-field class="full" subscriptSizing="dynamic"><mat-label>{{ 'app.search' | t }}</mat-label>
        <input matInput [formControl]="search" /><mat-icon matSuffix>search</mat-icon></mat-form-field>
      <div class="list">
        @for (i of found(); track i.id) {
          <mat-checkbox [checked]="chosen().has(i.id)" (change)="toggle(i.id, $event.checked)">
            <span class="ltr">{{ i.sku }}</span> · {{ i | bi }} · <span class="ltr">{{ i.salesPrice ?? 0 | money }}</span>
          </mat-checkbox>
        }
        @if (!found().length) { <p class="secondary">{{ 'app.none' | t }}</p> }
      </div>
      <mat-checkbox [formControl]="own">{{ 'gt.ownCommission' | t }}</mat-checkbox>
      @if (own.value) {
        <div class="row">
          <mat-form-field><mat-label>{{ 'gt.commissionType' | t }}</mat-label>
            <mat-select [formControl]="ownType"><mat-option value="Percent">{{ 'gt.type.Percent' | t }}</mat-option>
              <mat-option value="Fixed">{{ 'gt.type.Fixed' | t }}</mat-option></mat-select></mat-form-field>
          <mat-form-field><mat-label>{{ (ownType.value === 'Percent' ? 'gt.rate' : 'gt.amountPerBottle') | t }}</mat-label>
            <input matInput type="number" min="0" [formControl]="ownValue" dir="ltr" /></mat-form-field>
        </div>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="!chosen().size || saving()" (click)="save()">{{ 'gt.addCount' | t: { n: chosen().size } }}</button>
    </mat-dialog-actions>
  `,
  styles: `.full { width: 100%; } .list { display: flex; flex-direction: column; max-height: 320px; overflow-y: auto; margin-block: 8px; }
    .row { display: flex; gap: 12px; flex-wrap: wrap; } .secondary { color: var(--mat-sys-on-surface-variant); font-size: 13px; }`
})
export class AddPerfumesDialog implements OnInit {
  protected readonly data = inject<PartnerShop>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<AddPerfumesDialog, boolean>);
  private readonly api = inject(ConsignmentApi);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  protected readonly search = new FormControl('', { nonNullable: true });
  protected readonly own = new FormControl(false, { nonNullable: true });
  protected readonly ownType = new FormControl<CommissionType>(this.data.commissionType, { nonNullable: true });
  protected readonly ownValue = new FormControl(this.data.commission, { nonNullable: true });
  protected readonly found = signal<Item[]>([]);
  protected readonly chosen = signal(new Set<string>());
  protected readonly saving = signal(false);

  constructor() {
    this.search.valueChanges.pipe(debounceTime(300)).subscribe(() => this.find());
  }

  ngOnInit(): void {
    this.find();
  }

  private find(): void {
    this.trade.items({ search: this.search.value || undefined, type: 'Stock', active: true, pageSize: 50 }).subscribe((p) => this.found.set(p.items));
  }

  protected toggle(id: string, on: boolean): void {
    const next = new Set(this.chosen());
    if (on) next.add(id);
    else next.delete(id);
    this.chosen.set(next);
  }

  protected save(): void {
    this.saving.set(true);
    const own = this.own.value;
    this.api.addItems(this.data.supplierId, [...this.chosen()], own ? this.ownType.value : null, own ? Number(this.ownValue.value) : null).subscribe({
      next: () => this.ref.close(true),
      error: (e) => {
        this.saving.set(false);
        this.notify.error(e);
      }
    });
  }
}

interface MovementData {
  shop: PartnerShop;
  type: MovementType;
  items: ConsignedItem[];
}

/** Bottles received from the shop on trust, or handed back to it. Only the bottles with a quantity are recorded. */
@Component({
  selector: 'app-movement-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ (data.type === 'Receipt' ? 'gt.receive' : 'gt.giveBack') | t }} · {{ data.shop | bi }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="row">
        <mat-form-field><mat-label>{{ 'stock.warehouse' | t }}</mat-label>
          <mat-select formControlName="warehouseId">@for (w of warehouses(); track w.id) { <mat-option [value]="w.id">{{ w | bi }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>{{ 'app.date' | t }}</mat-label><input matInput type="date" formControlName="date" /></mat-form-field>
        <mat-form-field class="grow"><mat-label>{{ 'app.notes' | t }}</mat-label><input matInput formControlName="notes" /></mat-form-field>
      </form>
      <table class="line-editor">
        <thead><tr><th>{{ 'gt.perfume' | t }}</th><th class="num">{{ 'gt.withUs' | t }}</th><th class="num">{{ 'gt.bottles' | t }}</th></tr></thead>
        <tbody>
          @for (i of data.items; track i.itemId; let n = $index) {
            <tr><td><span class="ltr">{{ i.sku }}</span> · {{ i | bi }}</td><td class="num ltr">{{ i.onHand }}</td>
              <td class="num"><input class="qty ltr" type="number" min="0" [formControl]="quantities[n]" [attr.aria-label]="('gt.bottles' | t) + ' ' + i.sku" /></td></tr>
          }
        </tbody>
      </table>
      <p class="secondary">{{ (data.type === 'Receipt' ? 'gt.receiveHint' : 'gt.giveBackHint') | t }}</p>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid || !total() || saving()" (click)="save()">{{ 'gt.saveBottles' | t: { n: total() } }}</button>
    </mat-dialog-actions>
  `,
  styles: `.row { display: flex; gap: 12px; flex-wrap: wrap; } .grow { flex: 1; min-width: 200px; }
    .qty { width: 80px; padding: 6px; border: 1px solid var(--mat-sys-outline-variant); border-radius: 6px; background: transparent; color: inherit; text-align: end; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 13px; }`
})
export class MovementDialog implements OnInit {
  protected readonly data = inject<MovementData>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<MovementDialog, Movement>);
  private readonly api = inject(ConsignmentApi);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  protected readonly warehouses = signal<Master[]>([]);
  protected readonly saving = signal(false);
  protected readonly quantities = this.data.items.map(() => new FormControl<number | null>(null));
  private readonly tick = signal(0);
  protected readonly total = computed(() => (this.tick(), this.quantities.reduce((sum, q) => sum + Math.max(Number(q.value) || 0, 0), 0)));
  protected readonly form = inject(FormBuilder).nonNullable.group({
    warehouseId: ['', Validators.required],
    date: [todayIso(), Validators.required],
    notes: ['']
  });

  constructor() {
    for (const q of this.quantities) q.valueChanges.subscribe(() => this.tick.update((n) => n + 1));
  }

  ngOnInit(): void {
    this.trade.warehouses().subscribe((w) => {
      const active = w.filter((x) => x.isActive);
      this.warehouses.set(active);
      if (active.length) this.form.controls.warehouseId.setValue((active.find((x) => x.code === 'MAIN') ?? active[0]).id);
    });
  }

  protected save(): void {
    const v = this.form.getRawValue();
    const lines = this.data.items
      .map((i, n) => ({ itemId: i.itemId, quantity: Number(this.quantities[n].value) || 0 }))
      .filter((l) => l.quantity > 0);
    this.saving.set(true);
    this.api.move(this.data.type, { supplierId: this.data.shop.supplierId, warehouseId: v.warehouseId, date: v.date, notes: v.notes || null, lines }).subscribe({
      next: (m) => this.ref.close(m),
      error: (e) => {
        this.saving.set(false);
        this.notify.error(e);
      }
    });
  }
}

/** A receipt or return slip to print and have the shop sign: the bottles stay the shop's property. */
@Component({
  selector: 'app-goods-slip',
  imports: [...SHARED],
  template: `
    @if (movement(); as m) {
      <div class="page-header no-print">
        <a mat-icon-button [routerLink]="['/purchasing/partner-shops', m.supplierId]" [attr.aria-label]="'app.back' | t"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
        <span class="spacer"></span>
        <button mat-flat-button (click)="print()"><mat-icon>print</mat-icon> {{ 'gt.print' | t }}</button>
      </div>
      <div class="slip">
        <h1>{{ (m.movementType === 'Receipt' ? 'gt.slip.Receipt' : 'gt.slip.Return') | t }}</h1>
        <div class="facts">
          <span>{{ 'gt.docNo' | t }}: <b class="ltr">{{ m.movementNo }}</b></span>
          <span>{{ 'app.date' | t }}: <b class="ltr">{{ m.date | d }}</b></span>
          <span>{{ 'gt.shop' | t }}: <b>{{ m | bi:'supplierName' }}</b></span>
          @if (m.status === 'Reversed') { <span class="chip bad">{{ 'gt.status.Reversed' | t }}</span> }
        </div>
        <table class="line-editor">
          <thead><tr><th>#</th><th>{{ 'item.sku' | t }}</th><th>{{ 'gt.perfume' | t }}</th><th class="num">{{ 'gt.bottles' | t }}</th></tr></thead>
          <tbody>
            @for (l of m.lines; track l.itemId; let n = $index) {
              <tr><td class="ltr">{{ n + 1 }}</td><td class="ltr">{{ l.sku }}</td><td>{{ l | bi }}</td><td class="num ltr">{{ l.quantity }}</td></tr>
            }
            <tr class="total-row"><td colspan="3">{{ 'app.total' | t }}</td><td class="num ltr">{{ total(m) }}</td></tr>
          </tbody>
        </table>
        @if (m.notes) { <p>{{ m.notes }}</p> }
        <p class="note">{{ 'gt.slipNote' | t }}</p>
        <div class="signatures">
          <div><span>{{ 'gt.signShop' | t }}</span><i></i></div>
          <div><span>{{ 'gt.signUs' | t }}</span><i></i></div>
        </div>
      </div>
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: `
    .spacer { flex: 1; } .page-header { display: flex; align-items: center; gap: 8px; }
    .slip { max-width: 820px; } .note { color: var(--mat-sys-on-surface-variant); font-size: 13px; margin-block: 16px; }
    .signatures { display: flex; gap: 48px; flex-wrap: wrap; margin-block-start: 40px; }
    .signatures div { flex: 1; min-width: 220px; display: flex; flex-direction: column; gap: 40px; }
    .signatures i { border-block-end: 1px solid currentColor; }
  `
})
export class GoodsSlipPage implements OnInit {
  readonly movementId = input.required<string>();
  private readonly api = inject(ConsignmentApi);
  private readonly notify = inject(NotifyService);
  protected readonly movement = signal<Movement | null>(null);

  ngOnInit(): void {
    this.api.movement(this.movementId()).subscribe({ next: (m) => this.movement.set(m), error: (e) => this.notify.error(e) });
  }

  protected total(m: Movement): number {
    return m.lines.reduce((sum, l) => sum + l.quantity, 0);
  }

  protected print(): void {
    window.print();
  }
}

/**
 * A partner shop's statement: the period's sales by perfume, our commission and the shop's share. Opened from a period end (a preview that can
 * be settled) or from a settlement (as it was made, with its invoice).
 */
@Component({
  selector: 'app-shop-statement',
  imports: [...SHARED],
  template: `
    @if (statement(); as s) {
      <div class="page-header no-print">
        <a mat-icon-button [routerLink]="['/purchasing/partner-shops', s.supplierId]" [attr.aria-label]="'app.back' | t"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
        <span class="spacer"></span>
        <button mat-stroked-button (click)="print()"><mat-icon>print</mat-icon> {{ 'gt.print' | t }}</button>
        @if (!s.settlementId && canSettle && s.amount > 0) {
          <button mat-flat-button [disabled]="busy()" (click)="settle(s)"><mat-icon>task_alt</mat-icon> {{ 'gt.settle' | t }}</button> }
      </div>
      <div class="statement">
        <h1>{{ 'gt.statementTitle' | t }} · {{ s | bi:'supplierName' }}</h1>
        <div class="facts">
          <span>{{ 'gt.period' | t }}: <b class="ltr">{{ s.periodStart | d }} – {{ s.periodEnd | d }}</b></span>
          <span>{{ 'gt.commission' | t }}: <b class="ltr">{{ commission(s) }}</b></span>
          @if (s.settlementNo) { <span>{{ 'gt.docNo' | t }}: <b class="ltr">{{ s.settlementNo }}</b></span> }
          @if (s.invoiceNo) {
            <span>{{ 'gt.invoice' | t }}: <a class="ltr" [routerLink]="['/purchasing/invoices', s.supplierInvoiceId]">{{ s.invoiceNo }}</a>
              <span class="chip" [class.ok]="s.invoiceStatus === 'Paid'" [class.bad]="s.invoiceStatus === 'Reversed'">{{ 'gt.inv.' + s.invoiceStatus | t }}</span></span>
          } @else { <span class="chip warn">{{ 'gt.notSettled' | t }}</span> }
        </div>
        <div class="table-wrap">
          <table class="line-editor">
            <thead><tr><th>{{ 'item.sku' | t }}</th><th>{{ 'gt.perfume' | t }}</th><th class="num">{{ 'gt.sold' | t }}</th><th class="num">{{ 'gt.returned' | t }}</th>
              <th class="num">{{ 'gt.sales' | t }}</th><th class="num">{{ 'gt.commission' | t }}</th><th class="num">{{ 'gt.dueToShop' | t }}</th></tr></thead>
            <tbody>
              @for (l of s.lines; track l.itemId) {
                <tr><td class="ltr">{{ l.sku }}</td><td>{{ l | bi }}</td><td class="num ltr">{{ l.sold }}</td><td class="num ltr">{{ l.returned }}</td>
                  <td class="num ltr">{{ l.sales | money }}</td><td class="num ltr">{{ l.commission | money }}</td><td class="num ltr">{{ l.due | money }}</td></tr>
              }
              <tr class="total-row"><td colspan="4">{{ 'app.total' | t }}</td><td class="num ltr">{{ s.sales | money }}</td>
                <td class="num ltr">{{ s.commission | money }}</td><td class="num ltr">{{ s.due | money }}</td></tr>
              @if (s.adjustments !== 0) {
                <tr><td colspan="6">{{ 'gt.adjustments' | t }}</td><td class="num ltr">{{ s.adjustments | money }}</td></tr>
              }
              <tr class="total-row"><td colspan="6">{{ 'gt.amountDue' | t }}</td><td class="num ltr"><b>{{ s.amount | money }}</b></td></tr>
            </tbody>
          </table>
        </div>
        @if (!s.lines.length) { <p class="secondary">{{ 'gt.noSales' | t }}</p> }
        @if (s.paid !== null) { <p>{{ 'gt.paidSoFar' | t }}: <b class="ltr">{{ s.paid | money }}</b></p> }
        @if (s.onHand.length) {
          <h3>{{ 'gt.stillWithUs' | t }}</h3>
          <p class="ltr-list">@for (i of s.onHand; track i.itemId) { @if (i.onHand) { <span>{{ i | bi }}: <b class="ltr">{{ i.onHand }}</b></span> } }</p>
        }
        <div class="signatures">
          <div><span>{{ 'gt.signShop' | t }}</span><i></i></div>
          <div><span>{{ 'gt.signUs' | t }}</span><i></i></div>
        </div>
      </div>
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: `
    .spacer { flex: 1; } .page-header { display: flex; align-items: center; gap: 8px; }
    .statement { max-width: 960px; } .secondary { color: var(--mat-sys-on-surface-variant); font-size: 13px; }
    .ltr-list { display: flex; flex-wrap: wrap; gap: 6px 20px; }
    .signatures { display: flex; gap: 48px; flex-wrap: wrap; margin-block-start: 40px; }
    .signatures div { flex: 1; min-width: 220px; display: flex; flex-direction: column; gap: 40px; }
    .signatures i { border-block-end: 1px solid currentColor; }
  `
})
export class ShopStatementPage implements OnInit {
  readonly id = input<string>();
  readonly settlementId = input<string>();
  readonly periodEnd = input<string>();
  private readonly api = inject(ConsignmentApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly router = inject(Router);
  private readonly i18n = inject(I18nService);
  protected readonly canSettle = inject(AuthService).can('pur.invoices:post');
  protected readonly statement = signal<Statement | null>(null);
  protected readonly busy = signal(false);

  ngOnInit(): void {
    const settlement = this.settlementId();
    const source = settlement ? this.api.settlement(settlement) : this.api.preview(this.id()!, this.periodEnd() ?? lastMonthEnd());
    source.subscribe({ next: (s) => this.statement.set(s), error: (e) => this.notify.error(e) });
  }

  protected commission(s: Statement): string {
    return commissionText(s.commissionType, s.commissionRate, this.i18n.t('gt.perBottle'));
  }

  protected print(): void {
    window.print();
  }

  protected settle(s: Statement): void {
    this.dialog
      .open(ConfirmDialog, { data: { title: 'gt.settle', message: 'gt.settleConfirm' } })
      .afterClosed()
      .pipe(filter(Boolean), switchMap(() => {
        this.busy.set(true);
        return this.api.settle(s.supplierId, s.periodEnd);
      }))
      .subscribe({
        next: (done) => {
          this.notify.success();
          void this.router.navigate(['/purchasing/partner-shops/statements', done.settlementId]);
        },
        error: (e) => {
          this.busy.set(false);
          this.notify.error(e);
        }
      });
  }
}

/** Commission earned by month and partner shop, for the partners. */
@Component({
  selector: 'app-partner-commissions',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <a mat-icon-button routerLink="/purchasing/partner-shops" [attr.aria-label]="'app.back' | t"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
      <h1>{{ 'gt.commissions' | t }}</h1>
    </div>
    <form class="row" [formGroup]="form" (ngSubmit)="load()">
      <mat-form-field subscriptSizing="dynamic"><mat-label>{{ 'app.from' | t }}</mat-label><input matInput type="date" formControlName="periodStart" /></mat-form-field>
      <mat-form-field subscriptSizing="dynamic"><mat-label>{{ 'app.to' | t }}</mat-label><input matInput type="date" formControlName="periodEnd" /></mat-form-field>
      <button mat-flat-button type="submit" [disabled]="form.invalid">{{ 'gt.show' | t }}</button>
    </form>
    <div class="table-wrap">
      <table class="line-editor">
        <thead><tr><th>{{ 'gt.month' | t }}</th><th>{{ 'gt.shop' | t }}</th><th class="num">{{ 'gt.bottles' | t }}</th><th class="num">{{ 'gt.sales' | t }}</th>
          <th class="num">{{ 'gt.commission' | t }}</th><th class="num">{{ 'gt.dueToShop' | t }}</th></tr></thead>
        <tbody>
          @for (r of rows(); track r.month + r.supplierId) {
            <tr><td class="ltr">{{ r.month }}</td><td>{{ r | bi:'supplierName' }}</td><td class="num ltr">{{ r.sold }}</td><td class="num ltr">{{ r.sales | money }}</td>
              <td class="num ltr"><b>{{ r.commission | money }}</b></td><td class="num ltr">{{ r.due | money }}</td></tr>
          }
          @if (rows().length) {
            <tr class="total-row"><td colspan="2">{{ 'app.total' | t }}</td><td class="num ltr">{{ totals().sold }}</td><td class="num ltr">{{ totals().sales | money }}</td>
              <td class="num ltr">{{ totals().commission | money }}</td><td class="num ltr">{{ totals().due | money }}</td></tr>
          }
        </tbody>
      </table>
    </div>
    @if (!rows().length) { <p class="secondary">{{ 'app.none' | t }}</p> }
  `,
  styles: `.page-header { display: flex; align-items: center; gap: 8px; } .row { display: flex; gap: 12px; flex-wrap: wrap; align-items: center; margin-block-end: 12px; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 13px; }`
})
export class PartnerCommissionsPage implements OnInit {
  private readonly api = inject(ConsignmentApi);
  private readonly notify = inject(NotifyService);
  protected readonly rows = signal<CommissionRow[]>([]);
  protected readonly totals = computed(() => this.rows().reduce((t, r) => ({ sold: t.sold + r.sold, sales: t.sales + r.sales, commission: t.commission + r.commission,
    due: t.due + r.due }), { sold: 0, sales: 0, commission: 0, due: 0 }));
  protected readonly form = inject(FormBuilder).nonNullable.group({
    periodStart: [monthStartIso(Number(todayIso().slice(0, 4)), 1), Validators.required],
    periodEnd: [todayIso(), Validators.required]
  });

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    const v = this.form.getRawValue();
    this.api.commissions(v.periodStart, v.periodEnd).subscribe({ next: (r) => this.rows.set(r), error: (e) => this.notify.error(e) });
  }
}
