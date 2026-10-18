import { Component, computed, inject, signal } from '@angular/core';
import { FormArray, FormBuilder, FormControl, FormGroup, Validators } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { filter, switchMap } from 'rxjs';
import { AuthService } from '../../core/auth.service';
import { todayIso } from '../../core/dates';
import { NotifyService } from '../../core/notify.service';
import { TradeApi } from '../../core/trade.api';
import { Adjustment, AdjustmentReason, Item, Master, Transfer } from '../../core/trade.models';
import { ReverseDialog, ReverseResult } from '../../shared/dialogs';
import { SHARED } from '../../shared/shared';

type LineGroup = FormGroup<{
  itemId: FormControl<string>;
  quantity: FormControl<number | null>;
  unitCost: FormControl<number | null>;
}>;

/** Shared bits of the adjustment and transfer pages: stock items, warehouses, a line editor. */
abstract class StockDocumentPage {
  protected readonly auth = inject(AuthService);
  protected readonly trade = inject(TradeApi);
  protected readonly notify = inject(NotifyService);
  protected readonly dialog = inject(MatDialog);
  protected readonly fb = inject(FormBuilder).nonNullable;

  protected readonly items = signal<Item[]>([]);
  protected readonly warehouses = signal<Master[]>([]);
  protected readonly activeWarehouses = computed(() => this.warehouses().filter((w) => w.isActive));
  protected readonly showForm = signal(false);
  protected readonly busy = signal(false);
  protected readonly lines = new FormArray<LineGroup>([]);
  private readonly itemMap = computed(() => new Map(this.items().map((i) => [i.id, i])));
  private readonly warehouseMap = computed(() => new Map(this.warehouses().map((w) => [w.id, w])));

  constructor() {
    this.trade.items({ type: 'Stock', active: true, pageSize: 500 }).subscribe((r) => this.items.set(r.items));
    this.trade.warehouses().subscribe((w) => this.warehouses.set(w));
  }

  protected item(id: string): Item | undefined {
    return this.itemMap().get(id);
  }

  protected warehouseName(id: string): string {
    const w = this.warehouseMap().get(id);
    return w ? w.code : '';
  }

  protected addLine(): void {
    this.lines.push(
      this.fb.group({
        itemId: this.fb.control('', Validators.required),
        quantity: this.fb.control<number | null>(null, Validators.required),
        unitCost: this.fb.control<number | null>(null)
      }) as LineGroup
    );
  }

  protected removeLine(i: number): void {
    this.lines.removeAt(i);
  }

  protected resetLines(): void {
    this.lines.clear();
    this.addLine();
  }

  protected reverseDialog() {
    return this.dialog.open(ReverseDialog, { data: {} }).afterClosed().pipe(filter((r): r is ReverseResult => !!r));
  }
}

@Component({
  selector: 'app-adjustments',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'adj.title' | t }}</h1>
      @if (auth.can('inv.stock:post') && !showForm()) {
        <button mat-flat-button (click)="open()"><mat-icon>add</mat-icon> {{ 'adj.new' | t }}</button>
      }
    </div>

    @if (showForm()) {
      <mat-card appearance="outlined" class="form-card">
        <div class="section-title">{{ 'adj.new' | t }}</div>
        <form [formGroup]="form" class="form-grid">
          <mat-form-field>
            <mat-label>{{ 'stock.warehouse' | t }}</mat-label>
            <mat-select formControlName="warehouseId">
              @for (w of activeWarehouses(); track w.id) { <mat-option [value]="w.id">{{ w.code }} · {{ w | bi }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'app.date' | t }}</mat-label>
            <input matInput type="date" formControlName="date" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'adj.reason' | t }}</mat-label>
            <mat-select formControlName="reason">
              @for (r of reasons; track r) { <mat-option [value]="r">{{ 'adj.reason.' + r | t }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'app.notes' | t }}</mat-label>
            <input matInput formControlName="notes" />
          </mat-form-field>
        </form>
        <p class="secondary">{{ 'adj.hint' | t }}</p>
        <table class="line-editor">
          <thead><tr><th>{{ 'item.name' | t }}</th><th class="narrow">{{ 'stock.quantity' | t }}</th><th class="narrow">{{ 'adj.unitCost' | t }}</th><th></th></tr></thead>
          <tbody>
            @for (line of lines.controls; track line; let i = $index) {
              <tr [formGroup]="line">
                <td>
                  <mat-form-field>
                    <mat-select formControlName="itemId" (selectionChange)="suggestCost(line)">
                      @for (it of items(); track it.id) { <mat-option [value]="it.id"><span class="ltr">{{ it.sku }}</span> · {{ it | bi }}</mat-option> }
                    </mat-select>
                  </mat-form-field>
                </td>
                <td class="narrow"><mat-form-field><input matInput type="number" formControlName="quantity" dir="ltr" /></mat-form-field></td>
                <td class="narrow"><mat-form-field><input matInput type="number" min="0" formControlName="unitCost" dir="ltr" /></mat-form-field></td>
                <td><button mat-icon-button type="button" (click)="removeLine(i)" [disabled]="lines.length === 1"><mat-icon>delete</mat-icon></button></td>
              </tr>
            }
          </tbody>
        </table>
        <div class="actions spread">
          <button mat-button type="button" (click)="addLine()"><mat-icon>add</mat-icon> {{ 'doc.addLine' | t }}</button>
          <span class="spacer"></span>
          <button mat-button type="button" (click)="showForm.set(false)">{{ 'app.cancel' | t }}</button>
          <button mat-flat-button (click)="save()" [disabled]="form.invalid || lines.invalid || busy()">{{ 'doc.post' | t }}</button>
        </div>
      </mat-card>
    }

    <div class="table-wrap">
      <table mat-table [dataSource]="rows()" multiTemplateDataRows>
        <ng-container matColumnDef="no">
          <th mat-header-cell *matHeaderCellDef>{{ 'adj.no' | t }}</th>
          <td mat-cell *matCellDef="let a"><span class="ltr">{{ a.adjustmentNo }}</span></td>
        </ng-container>
        <ng-container matColumnDef="date">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.date' | t }}</th>
          <td mat-cell *matCellDef="let a" class="ltr">{{ a.adjustmentDate | d }}</td>
        </ng-container>
        <ng-container matColumnDef="warehouse">
          <th mat-header-cell *matHeaderCellDef>{{ 'stock.warehouse' | t }}</th>
          <td mat-cell *matCellDef="let a">{{ warehouseName(a.warehouseId) }}</td>
        </ng-container>
        <ng-container matColumnDef="reason">
          <th mat-header-cell *matHeaderCellDef>{{ 'adj.reason' | t }}</th>
          <td mat-cell *matCellDef="let a">{{ 'adj.reason.' + a.reason | t }}</td>
        </ng-container>
        <ng-container matColumnDef="value">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'grn.value' | t }}</th>
          <td mat-cell *matCellDef="let a" class="num ltr">{{ a.totalValue | money }}</td>
        </ng-container>
        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
          <td mat-cell *matCellDef="let a"><span class="chip" [class.ok]="a.status === 'Posted'">{{ 'doc.status.' + a.status | t }}</span></td>
        </ng-container>
        <ng-container matColumnDef="actions">
          <th mat-header-cell *matHeaderCellDef></th>
          <td mat-cell *matCellDef="let a">
            @if (a.status === 'Posted' && !a.stockCountId && auth.can('inv.stock:post')) {
              <button mat-icon-button (click)="$event.stopPropagation(); reverse(a)" [matTooltip]="'doc.reverse' | t"><mat-icon>undo</mat-icon></button>
            }
          </td>
        </ng-container>
        <ng-container matColumnDef="detail">
          <td mat-cell *matCellDef="let a" [attr.colspan]="columns.length" class="detail-cell">
            @if (expanded() === a.id) {
              <table class="detail">
                @for (l of a.lines; track l.lineNo) {
                  <tr><td><span class="ltr">{{ l.sku }}</span> · {{ l | bi }}</td><td class="num ltr">{{ l.quantity }}</td><td class="num ltr">{{ l.value | money }}</td></tr>
                }
              </table>
              @if (a.notes) { <div class="secondary">{{ a.notes }}</div> }
            }
          </td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns" class="clickable" (click)="expanded.set(expanded() === row.id ? null : row.id)"></tr>
        <tr mat-row *matRowDef="let row; columns: ['detail']" class="detail-row"></tr>
      </table>
      @if (rows().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `,
  styles: `
    .spread { align-items: center; margin-top: 8px; }
    .spacer { flex: 1; }
    .detail-row { height: 0; }
    .detail-cell { padding: 0 16px; border-bottom-width: 0; }
    .detail { width: 100%; margin: 4px 0 12px; border-collapse: collapse; }
    .detail td { padding: 4px 8px; border-bottom: 1px dashed var(--mat-sys-outline-variant); }
  `
})
export class AdjustmentsPage extends StockDocumentPage {
  protected readonly columns = ['no', 'date', 'warehouse', 'reason', 'value', 'status', 'actions'];
  protected readonly reasons: AdjustmentReason[] = ['Opening', 'Damage', 'Expiry', 'Other'];
  protected readonly rows = signal<Adjustment[]>([]);
  protected readonly expanded = signal<string | null>(null);
  protected readonly form = this.fb.group({
    warehouseId: ['', Validators.required],
    date: [todayIso(), Validators.required],
    reason: ['Opening' as AdjustmentReason, Validators.required],
    notes: ['']
  });

  constructor() {
    super();
    this.load();
  }

  protected open(): void {
    this.form.patchValue({ warehouseId: this.activeWarehouses()[0]?.id ?? '', date: todayIso() });
    this.resetLines();
    this.showForm.set(true);
  }

  protected suggestCost(line: LineGroup): void {
    const item = this.item(line.controls.itemId.value);
    if (item && line.controls.unitCost.value === null) line.controls.unitCost.setValue(item.averageCost || item.purchasePrice || null);
  }

  protected save(): void {
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.trade
      .createAdjustment({
        ...v,
        lines: this.lines.getRawValue().map((l) => ({
          itemId: l.itemId,
          quantity: Number(l.quantity),
          unitCost: Number(l.quantity) > 0 && l.unitCost !== null ? Number(l.unitCost) : null
        }))
      })
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.showForm.set(false);
          this.notify.success();
          this.load();
        },
        error: (e) => {
          this.busy.set(false);
          this.notify.error(e);
        }
      });
  }

  protected reverse(a: Adjustment): void {
    this.reverseDialog()
      .pipe(switchMap((r) => this.trade.reverseAdjustment(a.id, r.date, r.reason)))
      .subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  private load(): void {
    this.trade.adjustments().subscribe((r) => this.rows.set(r));
  }
}

@Component({
  selector: 'app-transfers',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'trf.title' | t }}</h1>
      @if (auth.can('inv.stock:post') && !showForm()) {
        <button mat-flat-button (click)="open()"><mat-icon>add</mat-icon> {{ 'trf.new' | t }}</button>
      }
    </div>

    @if (showForm()) {
      <mat-card appearance="outlined" class="form-card">
        <div class="section-title">{{ 'trf.new' | t }}</div>
        <form [formGroup]="form" class="form-grid">
          <mat-form-field>
            <mat-label>{{ 'trf.from' | t }}</mat-label>
            <mat-select formControlName="fromWarehouseId">
              @for (w of activeWarehouses(); track w.id) { <mat-option [value]="w.id">{{ w.code }} · {{ w | bi }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'trf.to' | t }}</mat-label>
            <mat-select formControlName="toWarehouseId">
              @for (w of activeWarehouses(); track w.id) { <mat-option [value]="w.id">{{ w.code }} · {{ w | bi }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'app.date' | t }}</mat-label>
            <input matInput type="date" formControlName="date" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'app.notes' | t }}</mat-label>
            <input matInput formControlName="notes" />
          </mat-form-field>
        </form>
        <table class="line-editor">
          <thead><tr><th>{{ 'item.name' | t }}</th><th class="narrow">{{ 'stock.quantity' | t }}</th><th></th></tr></thead>
          <tbody>
            @for (line of lines.controls; track line; let i = $index) {
              <tr [formGroup]="line">
                <td>
                  <mat-form-field>
                    <mat-select formControlName="itemId">
                      @for (it of items(); track it.id) { <mat-option [value]="it.id"><span class="ltr">{{ it.sku }}</span> · {{ it | bi }}</mat-option> }
                    </mat-select>
                  </mat-form-field>
                </td>
                <td class="narrow"><mat-form-field><input matInput type="number" min="0" formControlName="quantity" dir="ltr" /></mat-form-field></td>
                <td><button mat-icon-button type="button" (click)="removeLine(i)" [disabled]="lines.length === 1"><mat-icon>delete</mat-icon></button></td>
              </tr>
            }
          </tbody>
        </table>
        <div class="actions spread">
          <button mat-button type="button" (click)="addLine()"><mat-icon>add</mat-icon> {{ 'doc.addLine' | t }}</button>
          <span class="spacer"></span>
          <button mat-button type="button" (click)="showForm.set(false)">{{ 'app.cancel' | t }}</button>
          <button mat-flat-button (click)="save()" [disabled]="form.invalid || lines.invalid || sameWarehouse() || busy()">{{ 'doc.post' | t }}</button>
        </div>
      </mat-card>
    }

    <div class="table-wrap">
      <table mat-table [dataSource]="rows()">
        <ng-container matColumnDef="no">
          <th mat-header-cell *matHeaderCellDef>{{ 'trf.no' | t }}</th>
          <td mat-cell *matCellDef="let x"><span class="ltr">{{ x.transferNo }}</span></td>
        </ng-container>
        <ng-container matColumnDef="date">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.date' | t }}</th>
          <td mat-cell *matCellDef="let x" class="ltr">{{ x.transferDate | d }}</td>
        </ng-container>
        <ng-container matColumnDef="route">
          <th mat-header-cell *matHeaderCellDef>{{ 'trf.from' | t }} → {{ 'trf.to' | t }}</th>
          <td mat-cell *matCellDef="let x">{{ warehouseName(x.fromWarehouseId) }} → {{ warehouseName(x.toWarehouseId) }}</td>
        </ng-container>
        <ng-container matColumnDef="lines">
          <th mat-header-cell *matHeaderCellDef>{{ 'doc.lines' | t }}</th>
          <td mat-cell *matCellDef="let x">
            @for (l of x.lines; track l.lineNo) { <div><span class="ltr">{{ l.sku }}</span> × <span class="ltr">{{ l.quantity }}</span></div> }
          </td>
        </ng-container>
        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
          <td mat-cell *matCellDef="let x"><span class="chip" [class.ok]="x.status === 'Posted'">{{ 'doc.status.' + x.status | t }}</span></td>
        </ng-container>
        <ng-container matColumnDef="actions">
          <th mat-header-cell *matHeaderCellDef></th>
          <td mat-cell *matCellDef="let x">
            @if (x.status === 'Posted' && auth.can('inv.stock:post')) {
              <button mat-icon-button (click)="reverse(x)" [matTooltip]="'doc.reverse' | t"><mat-icon>undo</mat-icon></button>
            }
          </td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
      </table>
      @if (rows().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `,
  styles: `
    .spread { align-items: center; margin-top: 8px; }
    .spacer { flex: 1; }
  `
})
export class TransfersPage extends StockDocumentPage {
  protected readonly columns = ['no', 'date', 'route', 'lines', 'status', 'actions'];
  protected readonly rows = signal<Transfer[]>([]);
  protected readonly form = this.fb.group({
    fromWarehouseId: ['', Validators.required],
    toWarehouseId: ['', Validators.required],
    date: [todayIso(), Validators.required],
    notes: ['']
  });
  protected readonly sameWarehouse = () => this.form.value.fromWarehouseId === this.form.value.toWarehouseId;

  constructor() {
    super();
    this.load();
  }

  protected open(): void {
    const [a, b] = this.activeWarehouses();
    this.form.patchValue({ fromWarehouseId: a?.id ?? '', toWarehouseId: b?.id ?? '', date: todayIso() });
    this.resetLines();
    this.showForm.set(true);
  }

  protected save(): void {
    this.busy.set(true);
    this.trade
      .createTransfer({
        ...this.form.getRawValue(),
        lines: this.lines.getRawValue().map((l) => ({ itemId: l.itemId, quantity: Number(l.quantity) }))
      })
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.showForm.set(false);
          this.notify.success();
          this.load();
        },
        error: (e) => {
          this.busy.set(false);
          this.notify.error(e);
        }
      });
  }

  protected reverse(x: Transfer): void {
    this.reverseDialog()
      .pipe(switchMap((r) => this.trade.reverseTransfer(x.id, r.date, r.reason)))
      .subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  private load(): void {
    this.trade.transfers().subscribe((r) => this.rows.set(r));
  }
}
