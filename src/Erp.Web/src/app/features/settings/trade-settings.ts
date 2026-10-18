import { Component, inject, signal, WritableSignal } from '@angular/core';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { Observable } from 'rxjs';
import { AuthService } from '../../core/auth.service';
import { NotifyService } from '../../core/notify.service';
import { SalesApi } from '../../core/sales.api';
import { CreditControl } from '../../core/sales.models';
import { TradeApi } from '../../core/trade.api';
import { Master } from '../../core/trade.models';
import { SHARED } from '../../shared/shared';

type MasterKind = 'warehouses' | 'groups' | 'uoms';

@Component({
  selector: 'app-trade-settings',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'tset.title' | t }}</h1></div>

    <div class="cards">
      <mat-card appearance="outlined" class="form-card">
        <div class="section-title">{{ 'nav.orders' | t }}</div>
        <form [formGroup]="purchase" class="form-grid" (ngSubmit)="savePurchase()">
          <mat-form-field>
            <mat-label>{{ 'tset.tolerance' | t }}</mat-label>
            <input matInput type="number" min="0" formControlName="priceTolerancePercent" dir="ltr" />
          </mat-form-field>
          <p class="secondary">{{ 'tset.approvalsMoved' | t }} <a routerLink="/settings/approvals">{{ 'nav.approvalRules' | t }}</a></p>
          @if (canManage) {
            <div class="actions"><button mat-flat-button [disabled]="purchase.invalid">{{ 'app.save' | t }}</button></div>
          }
        </form>
      </mat-card>
      @if (canReadSales) {
        <mat-card appearance="outlined" class="form-card">
          <div class="section-title">{{ 'nav.sales' | t }}</div>
          <form [formGroup]="salesForm" class="form-grid" (ngSubmit)="saveSales()">
            <mat-form-field class="full">
              <mat-label>{{ 'tset.creditControl' | t }}</mat-label>
              <mat-select formControlName="creditControl">
                @for (c of creditPolicies; track c) { <mat-option [value]="c">{{ 'tset.credit.' + c | t }}</mat-option> }
              </mat-select>
              <mat-hint>{{ 'tset.credit.' + salesForm.controls.creditControl.value + 'Hint' | t }}</mat-hint>
            </mat-form-field>
            <mat-form-field>
              <mat-label>{{ 'tset.quoteValidity' | t }}</mat-label>
              <input matInput type="number" min="1" max="365" formControlName="quotationValidityDays" dir="ltr" />
            </mat-form-field>
            @if (canManageSales) {
              <div class="actions"><button mat-flat-button [disabled]="salesForm.invalid">{{ 'app.save' | t }}</button></div>
            }
          </form>
        </mat-card>
      }
      <mat-card appearance="outlined" class="form-card">
        <div class="section-title">{{ 'nav.stock' | t }}</div>
        <mat-slide-toggle [formControl]="negative" (change)="saveNegative()">{{ 'tset.negative' | t }}</mat-slide-toggle>
        <p class="secondary">{{ 'tset.negativeHint' | t }}</p>
      </mat-card>
    </div>

    @for (section of sections; track section.kind) {
      <div class="section-title">{{ section.title | t }}</div>
      <div class="table-wrap">
        <table class="masters">
          <thead><tr><th>{{ 'tset.code' | t }}</th><th>{{ 'sup.nameAr' | t }}</th><th>{{ 'sup.nameEn' | t }}</th>@if (section.kind !== 'uoms') { <th>{{ 'item.active' | t }}</th> }<th></th></tr></thead>
          <tbody>
            @for (m of lists[section.kind](); track m.id) {
              <tr>
                <td class="ltr">{{ m.code }}</td><td>{{ m.nameAr }}</td><td>{{ m.nameEn }}</td>
                @if (section.kind !== 'uoms') {
                  <td><mat-slide-toggle [checked]="m.isActive" [disabled]="!canManage" (change)="toggle(section.kind, m, $event.checked)" /></td>
                }
                <td></td>
              </tr>
            }
            @if (canManage) {
              <tr class="new" [formGroup]="drafts[section.kind]">
                <td><input formControlName="code" dir="ltr" [placeholder]="'tset.code' | t" /></td>
                <td><input formControlName="nameAr" dir="rtl" [placeholder]="'sup.nameAr' | t" /></td>
                <td><input formControlName="nameEn" dir="ltr" [placeholder]="'sup.nameEn' | t" /></td>
                @if (section.kind !== 'uoms') { <td></td> }
                <td><button mat-stroked-button (click)="add(section.kind)" [disabled]="drafts[section.kind].invalid"><mat-icon>add</mat-icon> {{ 'tset.add' | t }}</button></td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styles: `
    .cards { display: grid; gap: 16px; grid-template-columns: repeat(auto-fit, minmax(320px, 1fr)); }
    .masters { border-collapse: collapse; }
    .masters th, .masters td { padding: 8px 12px; border-bottom: 1px solid var(--mat-sys-outline-variant); text-align: start; }
    .masters th { font: var(--mat-sys-label-large); color: var(--mat-sys-on-surface-variant); }
    .new input { width: 100%; padding: 8px; border: 1px solid var(--mat-sys-outline); border-radius: 8px; font: inherit;
      background: var(--mat-sys-surface); color: inherit; box-sizing: border-box; }
  `
})
export class TradeSettingsPage {
  private readonly auth = inject(AuthService);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  private readonly fb = inject(FormBuilder).nonNullable;

  private readonly sales = inject(SalesApi);
  protected readonly canManage = this.auth.can('inv.settings:manage') || this.auth.can('pur.settings:manage');
  protected readonly canReadSales = this.auth.can('sal.customers:read');
  protected readonly canManageSales = this.auth.can('sal.settings:manage');
  protected readonly creditPolicies: CreditControl[] = ['Block', 'Warn', 'None'];
  protected readonly salesForm = this.fb.group({
    creditControl: ['Block' as CreditControl, Validators.required],
    quotationValidityDays: [30, [Validators.required, Validators.min(1), Validators.max(365)]]
  });
  protected readonly sections: { kind: MasterKind; title: string }[] = [
    { kind: 'warehouses', title: 'tset.warehouses' },
    { kind: 'groups', title: 'tset.groups' },
    { kind: 'uoms', title: 'tset.uoms' }
  ];
  protected readonly lists: Record<MasterKind, WritableSignal<Master[]>> = {
    warehouses: signal<Master[]>([]),
    groups: signal<Master[]>([]),
    uoms: signal<Master[]>([])
  };
  protected readonly drafts = {
    warehouses: this.draft(),
    groups: this.draft(),
    uoms: this.draft()
  };
  protected readonly negative = new FormControl(false, { nonNullable: true });
  protected readonly purchase = this.fb.group({
    priceTolerancePercent: [2, [Validators.required, Validators.min(0)]]
  });

  constructor() {
    this.load('warehouses');
    this.load('groups');
    this.load('uoms');
    this.trade.inventorySettings().subscribe((s) => this.negative.setValue(s.allowNegativeStock));
    this.trade.purchaseSettings().subscribe((s) => this.purchase.patchValue(s));
    if (!this.canManage) {
      this.negative.disable();
      this.purchase.disable();
    }
    if (this.canReadSales) this.sales.settings().subscribe((s) => this.salesForm.patchValue(s));
    if (!this.canManageSales) this.salesForm.disable();
  }

  protected saveSales(): void {
    const v = this.salesForm.getRawValue();
    this.sales
      .saveSettings({ creditControl: v.creditControl, quotationValidityDays: Number(v.quotationValidityDays) })
      .subscribe({ next: () => this.notify.success(), error: (e) => this.notify.error(e) });
  }

  protected savePurchase(): void {
    const v = this.purchase.getRawValue();
    this.trade
      .savePurchaseSettings({ priceTolerancePercent: Number(v.priceTolerancePercent) })
      .subscribe({ next: () => this.notify.success(), error: (e) => this.notify.error(e) });
  }

  protected saveNegative(): void {
    this.trade.saveInventorySettings(this.negative.value).subscribe({ next: () => this.notify.success(), error: (e) => this.notify.error(e) });
  }

  protected add(kind: MasterKind): void {
    const body = { ...this.drafts[kind].getRawValue(), isActive: true };
    this.save(kind, null, body).subscribe({
      next: () => {
        this.drafts[kind].reset();
        this.notify.success();
        this.load(kind);
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected toggle(kind: MasterKind, m: Master, isActive: boolean): void {
    this.save(kind, m.id, { code: m.code, nameEn: m.nameEn, nameAr: m.nameAr, isActive, branchId: m.branchId ?? null }).subscribe({
      next: () => this.load(kind),
      error: (e) => {
        this.notify.error(e);
        this.load(kind);
      }
    });
  }

  private save(kind: MasterKind, id: string | null, body: unknown): Observable<Master> {
    return kind === 'warehouses' ? this.trade.saveWarehouse(id, body) : kind === 'groups' ? this.trade.saveGroup(id, body) : this.trade.createUom(body);
  }

  private load(kind: MasterKind): void {
    const source = kind === 'warehouses' ? this.trade.warehouses() : kind === 'groups' ? this.trade.groups() : this.trade.uoms();
    source.subscribe((list) => this.lists[kind].set(list));
  }

  private draft() {
    return this.fb.group({ code: ['', Validators.required], nameEn: [''], nameAr: [''] });
  }
}
