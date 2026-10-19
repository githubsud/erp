import { Component, effect, inject, input, signal } from '@angular/core';
import { FormArray, FormBuilder, FormControl, Validators } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { todayIso } from '../../core/dates';
import { NotifyService } from '../../core/notify.service';
import { TradeApi } from '../../core/trade.api';
import { Item, Master } from '../../core/trade.models';
import { SHARED } from '../../shared/shared';

interface Component_ {
  itemId: string;
  sku: string;
  nameEn: string;
  nameAr: string;
  uomCode: string;
  quantity: number;
  averageCost: number;
}

/** A kit's components (per kit unit) and building kits from them. */
@Component({
  selector: 'app-item-kit',
  imports: [...SHARED],
  template: `
    <p class="secondary">{{ 'kit.hint' | t }}</p>
    <table class="line-editor">
      <thead><tr><th>{{ 'kit.component' | t }}</th><th class="narrow">{{ 'kit.perKit' | t }}</th><th></th></tr></thead>
      <tbody>
        @for (l of lines.controls; track l; let i = $index) {
          <tr [formGroup]="l">
            <td><mat-form-field><mat-select formControlName="itemId" [disabled]="!canManage">@for (it of items(); track it.id) {
              <mat-option [value]="it.id"><span class="ltr">{{ it.sku }}</span> {{ it | bi }}</mat-option> }</mat-select></mat-form-field></td>
            <td class="narrow"><mat-form-field><input matInput type="number" formControlName="quantity" dir="ltr" [readonly]="!canManage" /></mat-form-field></td>
            <td>@if (canManage) { <button mat-icon-button (click)="lines.removeAt(i)"><mat-icon>delete</mat-icon></button> }</td>
          </tr>
        }
      </tbody>
    </table>
    @if (canManage) {
      <div class="row">
        <button mat-button (click)="add()"><mat-icon>add</mat-icon> {{ 'doc.addLine' | t }}</button>
        <button mat-stroked-button [disabled]="lines.invalid" (click)="save()">{{ 'app.save' | t }}</button>
      </div>
    }
    @if (saved().length && canPost) {
      <div class="row assemble">
        <mat-form-field><mat-label>{{ 'po.warehouse' | t }}</mat-label>
          <mat-select [formControl]="warehouse">@for (w of warehouses(); track w.id) { <mat-option [value]="w.id">{{ w | bi }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field class="narrow"><mat-label>{{ 'kit.quantity' | t }}</mat-label><input matInput type="number" min="1" [formControl]="quantity" dir="ltr" /></mat-form-field>
        <span class="secondary">{{ 'kit.estimated' | t }} <b class="ltr">{{ unitCost() * (quantity.value || 0) | money }}</b></span>
        <button mat-flat-button (click)="assemble()"><mat-icon>build</mat-icon> {{ 'kit.assemble' | t }}</button>
      </div>
    }
  `,
  styles: `.row { display: flex; flex-wrap: wrap; gap: 8px; align-items: baseline; margin-top: 8px; } .secondary { color: var(--mat-sys-on-surface-variant); }`
})
export class ItemKit {
  readonly itemId = input.required<string>();
  private readonly api = inject(ApiService);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  private readonly auth = inject(AuthService);
  private readonly fb = inject(FormBuilder).nonNullable;
  protected readonly canManage = this.auth.can('inv.items:manage');
  protected readonly canPost = this.auth.can('inv.stock:post');
  protected readonly items = signal<Item[]>([]);
  protected readonly warehouses = signal<Master[]>([]);
  protected readonly saved = signal<Component_[]>([]);
  protected readonly unitCost = signal(0);
  protected readonly lines = new FormArray<ReturnType<ItemKit['line']>>([]);
  protected readonly warehouse = new FormControl('', { nonNullable: true });
  protected readonly quantity = new FormControl(1, { nonNullable: true });

  constructor() {
    this.trade.items({ type: 'Stock', active: true, pageSize: 500 }).subscribe((r) => this.items.set(r.items.filter((i) => i.id !== this.itemId())));
    this.trade.warehouses().subscribe((w) => {
      this.warehouses.set(w.filter((x) => x.isActive));
      this.warehouse.setValue(w.find((x) => x.code === 'MAIN')?.id ?? w[0]?.id ?? '');
    });
    effect(() => this.load(this.itemId()));
  }

  private line(itemId = '', quantity = 1) {
    return this.fb.group({ itemId: [itemId, Validators.required], quantity: [quantity, [Validators.required, Validators.min(0.000001)]] });
  }

  private load(id: string): void {
    this.api.get<Component_[]>(`inventory/items/${id}/components`).subscribe((c) => this.show(c));
  }

  private show(c: Component_[]): void {
    this.saved.set(c);
    this.unitCost.set(c.reduce((s, x) => s + x.quantity * x.averageCost, 0));
    this.lines.clear();
    for (const x of c) this.lines.push(this.line(x.itemId, x.quantity));
  }

  protected add(): void {
    this.lines.push(this.line());
  }

  protected save(): void {
    const body = this.lines.getRawValue().map((l) => ({ itemId: l.itemId, quantity: Number(l.quantity) }));
    this.api.put<Component_[]>(`inventory/items/${this.itemId()}/components`, body).subscribe({
      next: (c) => {
        this.show(c);
        this.notify.success();
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected assemble(): void {
    this.api.post<{ assemblyNo: string; totalValue: number }>('inventory/assemblies', {
      kitItemId: this.itemId(), warehouseId: this.warehouse.value, date: todayIso(), quantity: Number(this.quantity.value), notes: null
    }).subscribe({
      next: () => {
        this.notify.success();
        this.load(this.itemId());
      },
      error: (e) => this.notify.error(e)
    });
  }
}
