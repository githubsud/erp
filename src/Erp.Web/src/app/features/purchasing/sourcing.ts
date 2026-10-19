import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { FormArray, FormBuilder, FormControl, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { Router } from '@angular/router';
import { filter, forkJoin } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { addDaysIso, todayIso } from '../../core/dates';
import { I18nService } from '../../core/i18n/i18n.service';
import { NotifyService } from '../../core/notify.service';
import { TradeApi } from '../../core/trade.api';
import { Item, Master, Supplier } from '../../core/trade.models';
import { SHARED } from '../../shared/shared';

interface RfqRow {
  id: string;
  rfqNo: string;
  rfqDate: string;
  replyBy: string | null;
  status: string;
  lines: number;
  suppliers: number;
}

interface BlanketRow {
  id: string;
  agreementNo: string;
  supplierNameEn: string;
  supplierNameAr: string;
  validFrom: string;
  validTo: string;
  status: string;
}

interface Cell {
  supplierId: string;
  unitPrice: number | null;
  amount: number | null;
  leadTimeDays: number | null;
  lowest: boolean;
  awarded: boolean;
}

interface Comparison {
  id: string;
  rfqNo: string;
  rfqDate: string;
  replyBy: string | null;
  status: 'Open' | 'Awarded' | 'Cancelled';
  notes: string | null;
  suppliers: { supplierId: string; code: string; nameEn: string; nameAr: string; quotedLines: number; total: number; complete: boolean;
    purchaseOrderId: string | null; poNo: string | null }[];
  lines: { rfqLineId: string; lineNo: number; itemId: string; sku: string; nameEn: string; nameAr: string; description: string | null; quantity: number;
    cells: Cell[] }[];
  bestMixTotal: number;
  cheapestCompleteSupplierId: string | null;
}

interface Blanket {
  id: string;
  agreementNo: string;
  supplierId: string;
  supplierNameEn: string;
  supplierNameAr: string;
  validFrom: string;
  validTo: string;
  status: string;
  lines: { id: string; itemId: string; sku: string; nameEn: string; nameAr: string; unitPrice: number; maxQuantity: number; released: number; remaining: number }[];
  agreedValue: number;
  releasedValue: number;
  releases: { id: string; poNo: string; orderDate: string; status: string; total: number }[];
}

@Component({
  selector: 'app-sourcing',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'src.title' | t }}</h1>
      <span class="spacer"></span>
      @if (canManage) {
        <button mat-flat-button (click)="newRfq()"><mat-icon>add</mat-icon> {{ 'src.newRfq' | t }}</button>
        <button mat-stroked-button (click)="newBlanket()"><mat-icon>add</mat-icon> {{ 'src.newBlanket' | t }}</button>
      }
    </div>
    <mat-tab-group mat-stretch-tabs="false">
      <mat-tab [label]="'src.rfqs' | t">
        <table class="line-editor tab-body">
          <thead><tr><th>{{ 'src.rfqNo' | t }}</th><th>{{ 'app.date' | t }}</th><th>{{ 'src.replyBy' | t }}</th><th class="num">{{ 'src.lines' | t }}</th>
            <th class="num">{{ 'src.suppliers' | t }}</th><th>{{ 'app.status' | t }}</th></tr></thead>
          <tbody>
            @for (r of rfqs(); track r.id) {
              <tr class="clickable" [routerLink]="['/purchasing/rfqs', r.id]">
                <td class="ltr">{{ r.rfqNo }}</td><td class="ltr">{{ r.rfqDate | d }}</td><td class="ltr">{{ r.replyBy | d }}</td>
                <td class="num ltr">{{ r.lines }}</td><td class="num ltr">{{ r.suppliers }}</td>
                <td><span class="chip" [class.warn]="r.status === 'Open'" [class.ok]="r.status === 'Awarded'">{{ 'src.status.' + r.status | t }}</span></td>
              </tr>
            }
          </tbody>
        </table>
        @if (!rfqs().length) { <p class="secondary">{{ 'app.none' | t }}</p> }
      </mat-tab>
      <mat-tab [label]="'src.blankets' | t">
        <table class="line-editor tab-body">
          <thead><tr><th>{{ 'src.agreementNo' | t }}</th><th>{{ 'po.supplier' | t }}</th><th>{{ 'src.valid' | t }}</th><th>{{ 'app.status' | t }}</th></tr></thead>
          <tbody>
            @for (b of blankets(); track b.id) {
              <tr class="clickable" [routerLink]="['/purchasing/blanket-agreements', b.id]">
                <td class="ltr">{{ b.agreementNo }}</td><td>{{ b | bi:'supplierName' }}</td><td class="ltr">{{ b.validFrom | d }} – {{ b.validTo | d }}</td>
                <td><span class="chip" [class.ok]="b.status === 'Active'">{{ 'src.status.' + b.status | t }}</span></td>
              </tr>
            }
          </tbody>
        </table>
        @if (!blankets().length) { <p class="secondary">{{ 'app.none' | t }}</p> }
      </mat-tab>
    </mat-tab-group>
  `,
  styles: `.spacer { flex: 1; } .clickable { cursor: pointer; } .tab-body { margin-block-start: 16px; } .secondary { color: var(--mat-sys-on-surface-variant); }`
})
export class SourcingPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  protected readonly canManage = inject(AuthService).can('pur.orders:manage');
  protected readonly rfqs = signal<RfqRow[]>([]);
  protected readonly blankets = signal<BlanketRow[]>([]);

  ngOnInit(): void {
    this.api.get<RfqRow[]>('purchasing/rfqs').subscribe((r) => this.rfqs.set(r));
    this.api.get<BlanketRow[]>('purchasing/blanket-agreements').subscribe((b) => this.blankets.set(b));
  }

  protected newRfq(): void {
    this.dialog.open(SourcingDialog, { data: 'rfq', width: '760px' }).afterClosed().pipe(filter(Boolean))
      .subscribe((id: string) => void this.router.navigate(['/purchasing/rfqs', id]));
  }

  protected newBlanket(): void {
    this.dialog.open(SourcingDialog, { data: 'blanket', width: '760px' }).afterClosed().pipe(filter(Boolean))
      .subscribe((id: string) => void this.router.navigate(['/purchasing/blanket-agreements', id]));
  }
}

/** New RFQ (items, quantities and invited suppliers) or new blanket agreement (one supplier, items, prices and maximum quantities). */
@Component({
  selector: 'app-sourcing-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ (kind === 'rfq' ? 'src.newRfq' : 'src.newBlanket') | t }}</h2>
    <mat-dialog-content>
      <form [formGroup]="head" class="form-grid">
        @if (kind === 'rfq') {
          <mat-form-field><mat-label>{{ 'src.replyBy' | t }}</mat-label><input matInput type="date" formControlName="replyBy" /></mat-form-field>
          <mat-form-field><mat-label>{{ 'po.warehouse' | t }}</mat-label>
            <mat-select formControlName="warehouseId">@for (w of warehouses(); track w.id) { <mat-option [value]="w.id">{{ w | bi }}</mat-option> }</mat-select></mat-form-field>
          <mat-form-field class="wide"><mat-label>{{ 'src.suppliers' | t }}</mat-label>
            <mat-select formControlName="supplierIds" multiple>@for (s of suppliers(); track s.id) { <mat-option [value]="s.id">{{ s | bi }}</mat-option> }</mat-select></mat-form-field>
        } @else {
          <mat-form-field><mat-label>{{ 'po.supplier' | t }}</mat-label>
            <mat-select formControlName="supplierId">@for (s of qarSuppliers(); track s.id) { <mat-option [value]="s.id">{{ s | bi }}</mat-option> }</mat-select></mat-form-field>
          <mat-form-field><mat-label>{{ 'app.from' | t }}</mat-label><input matInput type="date" formControlName="validFrom" /></mat-form-field>
          <mat-form-field><mat-label>{{ 'app.to' | t }}</mat-label><input matInput type="date" formControlName="validTo" /></mat-form-field>
        }
      </form>
      <table class="line-editor">
        <thead><tr><th>{{ 'src.item' | t }}</th><th class="narrow">{{ (kind === 'rfq' ? 'po.qty' : 'src.maxQty') | t }}</th>
          @if (kind === 'blanket') { <th class="narrow">{{ 'src.price' | t }}</th> }<th></th></tr></thead>
        <tbody>
          @for (l of lines.controls; track l; let i = $index) {
            <tr [formGroup]="l">
              <td><mat-form-field><mat-select formControlName="itemId">@for (it of items(); track it.id) {
                <mat-option [value]="it.id"><span class="ltr">{{ it.sku }}</span> {{ it | bi }}</mat-option> }</mat-select></mat-form-field></td>
              <td class="narrow"><mat-form-field><input matInput type="number" formControlName="quantity" dir="ltr" /></mat-form-field></td>
              @if (kind === 'blanket') { <td class="narrow"><mat-form-field><input matInput type="number" formControlName="unitPrice" dir="ltr" /></mat-form-field></td> }
              <td><button mat-icon-button (click)="lines.removeAt(i)"><mat-icon>delete</mat-icon></button></td>
            </tr>
          }
        </tbody>
      </table>
      <button mat-button (click)="addLine()"><mat-icon>add</mat-icon> {{ 'doc.addLine' | t }}</button>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="!valid()" (click)="save()">{{ 'app.save' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `.wide { min-width: 320px; }`
})
export class SourcingDialog implements OnInit {
  protected readonly kind = inject<'rfq' | 'blanket'>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<SourcingDialog, string>);
  private readonly api = inject(ApiService);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  private readonly fb = inject(FormBuilder).nonNullable;
  protected readonly suppliers = signal<Supplier[]>([]);
  protected readonly qarSuppliers = computed(() => this.suppliers().filter((s) => s.currency === 'QAR'));
  protected readonly warehouses = signal<Master[]>([]);
  protected readonly items = signal<Item[]>([]);
  protected readonly head = this.fb.group({
    replyBy: [addDaysIso(todayIso(), 7)], warehouseId: [''], supplierIds: [[] as string[]], supplierId: [''],
    validFrom: [todayIso()], validTo: [addDaysIso(todayIso(), 365)]
  });
  protected readonly lines = new FormArray<ReturnType<SourcingDialog['line']>>([]);

  ngOnInit(): void {
    forkJoin([this.trade.suppliers(), this.trade.warehouses(), this.trade.items({ active: true, pageSize: 500 })]).subscribe(([s, w, i]) => {
      this.suppliers.set(s.filter((x) => x.isActive));
      this.warehouses.set(w.filter((x) => x.isActive));
      this.items.set(i.items);
      this.head.patchValue({ warehouseId: w.find((x) => x.code === 'MAIN')?.id ?? w[0]?.id ?? '' });
    });
    this.addLine();
  }

  private line() {
    return this.fb.group({ itemId: ['', Validators.required], quantity: [1, [Validators.required, Validators.min(0.000001)]], unitPrice: [0, Validators.min(0)] });
  }

  protected addLine(): void {
    this.lines.push(this.line());
  }

  protected valid(): boolean {
    const h = this.head.getRawValue();
    const ok = this.lines.length > 0 && this.lines.valid;
    return this.kind === 'rfq' ? ok && !!h.warehouseId && h.supplierIds.length > 0 : ok && !!h.supplierId;
  }

  protected save(): void {
    const h = this.head.getRawValue();
    const lines = this.lines.getRawValue();
    const req = this.kind === 'rfq'
      ? this.api.post<{ id: string }>('purchasing/rfqs', {
          rfqDate: todayIso(), replyBy: h.replyBy || null, warehouseId: h.warehouseId, notes: null, supplierIds: h.supplierIds,
          lines: lines.map((l) => ({ itemId: l.itemId, description: null, quantity: Number(l.quantity) }))
        })
      : this.api.post<{ id: string }>('purchasing/blanket-agreements', {
          supplierId: h.supplierId, validFrom: h.validFrom, validTo: h.validTo, notes: null,
          lines: lines.map((l) => ({ itemId: l.itemId, unitPrice: Number(l.unitPrice), maxQuantity: Number(l.quantity) }))
        });
    req.subscribe({ next: (r) => this.ref.close(r.id), error: (e) => this.notify.error(e) });
  }
}

/** Side-by-side comparison: enter each supplier's prices, then award the whole RFQ or line by line. */
@Component({
  selector: 'app-rfq-detail',
  imports: [...SHARED],
  template: `
    @if (cmp(); as c) {
      <div class="page-header">
        <a mat-icon-button routerLink="/purchasing/sourcing"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
        <h1><span class="ltr">{{ c.rfqNo }}</span></h1>
        <span class="chip" [class.warn]="c.status === 'Open'" [class.ok]="c.status === 'Awarded'">{{ 'src.status.' + c.status | t }}</span>
        <span class="spacer"></span>
        @if (open() && canManage) {
          <button mat-stroked-button (click)="cancel()">{{ 'app.cancel' | t }}</button>
          <button mat-flat-button [disabled]="!picks().size" (click)="award()"><mat-icon>gavel</mat-icon> {{ 'src.award' | t }}</button>
        }
      </div>
      @if (open()) { <p class="secondary">{{ 'src.compareHint' | t }}</p> }
      <div class="table-wrap">
        <table class="line-editor compare">
          <thead>
            <tr><th>{{ 'src.item' | t }}</th><th class="num">{{ 'po.qty' | t }}</th>
              @for (s of c.suppliers; track s.supplierId) {
                <th class="num">{{ s | bi }}
                  @if (s.supplierId === c.cheapestCompleteSupplierId) { <span class="chip ok">{{ 'src.cheapest' | t }}</span> }
                  @if (s.poNo) { <div><a class="ltr" [routerLink]="['/purchasing/orders', s.purchaseOrderId]">{{ s.poNo }}</a></div> }
                </th>
              }
            </tr>
          </thead>
          <tbody>
            @for (l of c.lines; track l.rfqLineId) {
              <tr>
                <td><span class="ltr secondary">{{ l.sku }}</span> {{ l | bi }}</td>
                <td class="num ltr">{{ l.quantity }}</td>
                @for (cell of l.cells; track cell.supplierId) {
                  <td class="num" [class.lowest]="cell.lowest" [class.picked]="picks().get(l.rfqLineId) === cell.supplierId || cell.awarded">
                    @if (open() && canManage) {
                      <input class="price ltr" type="number" min="0" [value]="cell.unitPrice ?? ''" (change)="price(cell.supplierId, l.rfqLineId, $any($event.target).value)" />
                      @if (cell.unitPrice !== null) {
                        <button mat-icon-button class="pick" (click)="pick(l.rfqLineId, cell.supplierId)"><mat-icon>{{ picks().get(l.rfqLineId) === cell.supplierId ? 'check_circle' : 'radio_button_unchecked' }}</mat-icon></button>
                      }
                    } @else {
                      <span class="ltr">{{ cell.unitPrice !== null ? (cell.unitPrice | money) : '—' }}</span>
                    }
                    @if (cell.amount !== null) { <div class="secondary ltr">{{ cell.amount | money }}</div> }
                  </td>
                }
              </tr>
            }
            <tr class="total-row"><td colspan="2">{{ 'app.total' | t }}</td>
              @for (s of c.suppliers; track s.supplierId) {
                <td class="num ltr">{{ s.total | money }} @if (!s.complete) { <div class="secondary">{{ s.quotedLines }}/{{ c.lines.length }}</div> }
                  @if (open() && canManage && s.complete) { <div><button mat-button (click)="pickAll(s.supplierId)">{{ 'src.pickAll' | t }}</button></div> }</td>
              }
            </tr>
          </tbody>
        </table>
      </div>
      <p>{{ 'src.bestMix' | t }}: <b class="ltr">{{ c.bestMixTotal | money }}</b></p>
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: `
    .spacer { flex: 1; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 12px; }
    .compare td, .compare th { vertical-align: top; }
    .price { width: 90px; padding: 4px; border: 1px solid var(--mat-sys-outline-variant); border-radius: 6px; background: transparent; color: inherit; font: inherit; }
    td.lowest { background: color-mix(in srgb, #2e7d32 10%, transparent); }
    td.picked { outline: 2px solid var(--mat-sys-primary); outline-offset: -2px; }
    .pick { vertical-align: middle; }
  `
})
export class RfqDetailPage implements OnInit {
  readonly id = input.required<string>();
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  private readonly i18n = inject(I18nService);
  private readonly router = inject(Router);
  protected readonly canManage = inject(AuthService).can('pur.orders:manage');
  protected readonly cmp = signal<Comparison | null>(null);
  protected readonly open = computed(() => this.cmp()?.status === 'Open');
  protected readonly picks = signal(new Map<string, string>());

  ngOnInit(): void {
    this.api.get<Comparison>(`purchasing/rfqs/${this.id()}`).subscribe({ next: (c) => this.cmp.set(c), error: (e) => this.notify.error(e) });
  }

  protected price(supplierId: string, lineId: string, value: string): void {
    const unitPrice = value === '' ? null : Number(value);
    this.api.put<Comparison>(`purchasing/rfqs/${this.id()}/quotes`, { supplierId, quotes: [{ rfqLineId: lineId, unitPrice, leadTimeDays: null }] })
      .subscribe({ next: (c) => this.cmp.set(c), error: (e) => this.notify.error(e) });
  }

  protected pick(lineId: string, supplierId: string): void {
    this.picks.update((m) => new Map(m).set(lineId, supplierId));
  }

  protected pickAll(supplierId: string): void {
    this.picks.set(new Map(this.cmp()!.lines.map((l) => [l.rfqLineId, supplierId])));
  }

  protected award(): void {
    if (!confirm(this.i18n.t('src.awardConfirm'))) return;
    this.api.post<{ purchaseOrderIds: string[] }>(`purchasing/rfqs/${this.id()}/award`, { lines: Object.fromEntries(this.picks()), orderDate: todayIso() })
      .subscribe({
        next: (r) => {
          this.notify.success();
          if (r.purchaseOrderIds.length === 1) void this.router.navigate(['/purchasing/orders', r.purchaseOrderIds[0]]);
          else this.ngOnInit();
        },
        error: (e) => this.notify.error(e)
      });
  }

  protected cancel(): void {
    this.api.post(`purchasing/rfqs/${this.id()}/cancel`, {}).subscribe({ next: () => this.ngOnInit(), error: (e) => this.notify.error(e) });
  }
}

/** A blanket agreement: what is agreed, what has been released, and releasing a new order. */
@Component({
  selector: 'app-blanket-detail',
  imports: [...SHARED],
  template: `
    @if (b(); as a) {
      <div class="page-header">
        <a mat-icon-button routerLink="/purchasing/sourcing"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
        <h1><span class="ltr">{{ a.agreementNo }}</span> · {{ a | bi:'supplierName' }}</h1>
        <span class="spacer"></span>
        @if (canManage && a.status === 'Active') { <button mat-stroked-button (click)="close()">{{ 'src.closeAgreement' | t }}</button> }
      </div>
      <p class="secondary"><span class="ltr">{{ a.validFrom | d }} – {{ a.validTo | d }}</span> · {{ 'src.released' | t }}
        <b class="ltr">{{ a.releasedValue | money }}</b> / <span class="ltr">{{ a.agreedValue | money }}</span></p>
      <table class="line-editor">
        <thead><tr><th>{{ 'src.item' | t }}</th><th class="num">{{ 'src.price' | t }}</th><th class="num">{{ 'src.maxQty' | t }}</th><th class="num">{{ 'src.released' | t }}</th>
          <th class="num">{{ 'src.remaining' | t }}</th>@if (active()) { <th class="narrow">{{ 'src.releaseQty' | t }}</th> }</tr></thead>
        <tbody>
          @for (l of a.lines; track l.id) {
            <tr>
              <td><span class="ltr secondary">{{ l.sku }}</span> {{ l | bi }}</td><td class="num ltr">{{ l.unitPrice | money }}</td>
              <td class="num ltr">{{ l.maxQuantity }}</td><td class="num ltr">{{ l.released }}</td><td class="num ltr"><b>{{ l.remaining }}</b></td>
              @if (active()) { <td class="narrow"><mat-form-field><input matInput type="number" min="0" [max]="l.remaining" [formControl]="qty[l.itemId]" dir="ltr" /></mat-form-field></td> }
            </tr>
          }
        </tbody>
      </table>
      @if (active()) {
        <div class="release">
          <mat-form-field><mat-label>{{ 'po.warehouse' | t }}</mat-label>
            <mat-select [formControl]="warehouse">@for (w of warehouses(); track w.id) { <mat-option [value]="w.id">{{ w | bi }}</mat-option> }</mat-select></mat-form-field>
          <button mat-flat-button (click)="release()">{{ 'src.release' | t }}</button>
        </div>
      }
      <h3>{{ 'src.releases' | t }}</h3>
      <table class="line-editor">
        <tbody>
          @for (r of a.releases; track r.id) {
            <tr><td><a class="ltr" [routerLink]="['/purchasing/orders', r.id]">{{ r.poNo }}</a></td><td class="ltr">{{ r.orderDate | d }}</td>
              <td>{{ 'po.status.' + r.status | t }}</td><td class="num ltr">{{ r.total | money }}</td></tr>
          }
        </tbody>
      </table>
      @if (!a.releases.length) { <p class="secondary">{{ 'app.none' | t }}</p> }
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: `.spacer { flex: 1; } .secondary { color: var(--mat-sys-on-surface-variant); } .release { display: flex; gap: 12px; align-items: baseline; margin-top: 12px; }`
})
export class BlanketDetailPage implements OnInit {
  readonly id = input.required<string>();
  private readonly api = inject(ApiService);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  protected readonly canManage = inject(AuthService).can('pur.orders:manage');
  protected readonly b = signal<Blanket | null>(null);
  protected readonly active = computed(() => this.canManage && this.b()?.status === 'Active' && this.b()!.validTo >= todayIso());
  protected readonly warehouses = signal<Master[]>([]);
  protected readonly warehouse = new FormControl('', { nonNullable: true });
  protected qty: Record<string, FormControl<number>> = {};

  ngOnInit(): void {
    this.load();
    this.trade.warehouses().subscribe((w) => {
      this.warehouses.set(w.filter((x) => x.isActive));
      this.warehouse.setValue(w.find((x) => x.code === 'MAIN')?.id ?? w[0]?.id ?? '');
    });
  }

  private load(): void {
    this.api.get<Blanket>(`purchasing/blanket-agreements/${this.id()}`).subscribe({
      next: (b) => {
        this.qty = Object.fromEntries(b.lines.map((l) => [l.itemId, new FormControl(0, { nonNullable: true })]));
        this.b.set(b);
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected release(): void {
    const lines = Object.entries(this.qty).map(([itemId, c]) => ({ itemId, quantity: Number(c.value) || 0 })).filter((l) => l.quantity > 0);
    if (!lines.length) return;
    this.api.post(`purchasing/blanket-agreements/${this.id()}/release`, { orderDate: todayIso(), warehouseId: this.warehouse.value, lines }).subscribe({
      next: () => {
        this.notify.success();
        this.load();
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected close(): void {
    this.api.post(`purchasing/blanket-agreements/${this.id()}/close`, {}).subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }
}
