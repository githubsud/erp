import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormArray, FormBuilder, FormControl, FormGroup, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { Router } from '@angular/router';
import { filter, switchMap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { todayIso } from '../../core/dates';
import { NotifyService } from '../../core/notify.service';
import { TradeApi } from '../../core/trade.api';
import { Item, Master, Supplier } from '../../core/trade.models';
import { ApprovalTrail } from '../../shared/approvals';
import { ConfirmDialog } from '../../shared/dialogs';
import { SHARED } from '../../shared/shared';

export interface PurchaseRequestLine {
  lineNo: number;
  itemId: string;
  sku: string;
  itemNameEn: string;
  itemNameAr: string;
  description: string | null;
  quantity: number;
  estimatedPrice: number;
  amount: number;
}

export interface PurchaseRequest {
  id: string;
  prNo: string;
  requestDate: string;
  requiredDate: string | null;
  warehouseId: string;
  status: 'Draft' | 'PendingApproval' | 'Approved' | 'Ordered' | 'Cancelled';
  estimatedTotal: number;
  notes: string | null;
  purchaseOrderId: string | null;
  lines: PurchaseRequestLine[];
}

type LineGroup = FormGroup<{ itemId: FormControl<string>; quantity: FormControl<number>; estimatedPrice: FormControl<number>; description: FormControl<string> }>;

@Component({
  selector: 'app-purchase-requests',
  imports: [...SHARED, ApprovalTrail],
  template: `
    <div class="page-header">
      <h1>{{ 'pr.title' | t }}</h1>
      <button mat-flat-button (click)="edit(null)"><mat-icon>add</mat-icon> {{ 'pr.new' | t }}</button>
    </div>
    <p class="secondary">{{ 'pr.hint' | t }}</p>
    <div class="table-wrap">
      <table mat-table [dataSource]="rows()" multiTemplateDataRows>
        <ng-container matColumnDef="no">
          <th mat-header-cell *matHeaderCellDef>{{ 'pr.no' | t }}</th>
          <td mat-cell *matCellDef="let r"><span class="ltr">{{ r.prNo }}</span></td>
        </ng-container>
        <ng-container matColumnDef="date">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.date' | t }}</th>
          <td mat-cell *matCellDef="let r" class="ltr">{{ r.requestDate | d }}</td>
        </ng-container>
        <ng-container matColumnDef="required">
          <th mat-header-cell *matHeaderCellDef>{{ 'pr.required' | t }}</th>
          <td mat-cell *matCellDef="let r" class="ltr">{{ r.requiredDate | d }}</td>
        </ng-container>
        <ng-container matColumnDef="items">
          <th mat-header-cell *matHeaderCellDef>{{ 'pr.items' | t }}</th>
          <td mat-cell *matCellDef="let r">{{ itemsText(r) }}</td>
        </ng-container>
        <ng-container matColumnDef="total">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'pr.estimated' | t }}</th>
          <td mat-cell *matCellDef="let r" class="num ltr">{{ r.estimatedTotal | money }}</td>
        </ng-container>
        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
          <td mat-cell *matCellDef="let r"><span class="chip" [class]="chip(r.status)">{{ 'pr.status.' + r.status | t }}</span></td>
        </ng-container>
        <ng-container matColumnDef="actions">
          <th mat-header-cell *matHeaderCellDef></th>
          <td mat-cell *matCellDef="let r" class="actions-cell">
            @if (r.status === 'Draft') {
              <button mat-icon-button (click)="edit(r); $event.stopPropagation()" [attr.aria-label]="'app.edit' | t"><mat-icon>edit</mat-icon></button>
              <button mat-stroked-button (click)="submit(r); $event.stopPropagation()">{{ 'po.submit' | t }}</button>
            }
            @if (r.status === 'Approved' && auth.can('pur.orders:manage')) {
              <button mat-flat-button (click)="convert(r); $event.stopPropagation()"><mat-icon>shopping_cart</mat-icon> {{ 'pr.convert' | t }}</button>
            }
            @if (r.purchaseOrderId) {
              <a mat-button [routerLink]="['/purchasing/orders', r.purchaseOrderId]" (click)="$event.stopPropagation()">{{ 'pr.openOrder' | t }}</a>
            }
            @if (r.status === 'Draft' || r.status === 'PendingApproval' || r.status === 'Approved') {
              <button mat-icon-button (click)="cancel(r); $event.stopPropagation()" [attr.aria-label]="'app.cancel' | t"><mat-icon>close</mat-icon></button>
            }
          </td>
        </ng-container>
        <ng-container matColumnDef="detail">
          <td mat-cell *matCellDef="let r" [attr.colspan]="columns.length">
            @if (expanded() === r.id) {
              <div class="detail">
                @for (l of r.lines; track l.lineNo) {
                  <div><span class="ltr">{{ l.sku }}</span> · {{ l | bi:'itemName' }} — <span class="ltr">{{ l.quantity }} × {{ l.estimatedPrice | money }}</span>
                    @if (l.description) { <span class="secondary">({{ l.description }})</span> }</div>
                }
                @if (r.notes) { <p class="secondary">{{ r.notes }}</p> }
                <app-approval-trail docType="purchase_request" [docId]="r.id" />
              </div>
            }
          </td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let r; columns: columns" class="clickable" (click)="expanded.set(expanded() === r.id ? null : r.id)"></tr>
        <tr mat-row *matRowDef="let r; columns: ['detail']" class="detail-row"></tr>
      </table>
      @if (!rows().length) { <p class="empty">{{ 'app.none' | t }}</p> }
    </div>
  `,
  styles: `
    .secondary { color: var(--mat-sys-on-surface-variant); }
    .clickable { cursor: pointer; }
    .detail-row { height: 0; }
    .detail { padding: 8px 0 16px; }
    .actions-cell { white-space: nowrap; text-align: end; }
    .empty { padding: 24px; color: var(--mat-sys-on-surface-variant); }
  `
})
export class PurchaseRequestsPage implements OnInit {
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly router = inject(Router);
  protected readonly rows = signal<PurchaseRequest[]>([]);
  protected readonly expanded = signal<string | null>(null);
  protected readonly columns = ['no', 'date', 'required', 'items', 'total', 'status', 'actions'];

  ngOnInit(): void {
    this.reload();
  }

  reload(): void {
    this.api.get<PurchaseRequest[]>('purchasing/requests').subscribe({ next: (r) => this.rows.set(r), error: (e) => this.notify.error(e) });
  }

  protected itemsText(r: PurchaseRequest): string {
    const first = r.lines[0];
    return first ? `${first.sku}${r.lines.length > 1 ? ` +${r.lines.length - 1}` : ''}` : '';
  }

  protected chip(status: string): string {
    return status === 'Ordered' ? 'ok' : status === 'PendingApproval' ? 'warn' : status === 'Cancelled' ? 'bad' : 'info';
  }

  protected edit(r: PurchaseRequest | null): void {
    this.dialog
      .open(PurchaseRequestDialog, { data: r, width: '820px' })
      .afterClosed()
      .pipe(filter(Boolean))
      .subscribe(() => this.reload());
  }

  protected submit(r: PurchaseRequest): void {
    this.api.post<PurchaseRequest>(`purchasing/requests/${r.id}/submit`).subscribe({
      next: (x) => {
        this.notify.success(x.status === 'Approved' ? 'pr.approvedNow' : 'pr.sentForApproval');
        this.reload();
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected cancel(r: PurchaseRequest): void {
    this.dialog
      .open(ConfirmDialog, { data: { title: 'pr.cancel', message: 'pr.cancelConfirm' } })
      .afterClosed()
      .pipe(filter(Boolean), switchMap(() => this.api.post(`purchasing/requests/${r.id}/cancel`)))
      .subscribe({ next: () => this.reload(), error: (e) => this.notify.error(e) });
  }

  protected convert(r: PurchaseRequest): void {
    this.dialog
      .open(ConvertDialog, { width: '420px' })
      .afterClosed()
      .pipe(
        filter((s): s is string => !!s),
        switchMap((supplierId) => this.api.post<{ purchaseOrderId: string }>(`purchasing/requests/${r.id}/convert`, { supplierId }))
      )
      .subscribe({
        next: (x) => void this.router.navigate(['/purchasing/orders', x.purchaseOrderId, 'edit']),
        error: (e) => this.notify.error(e)
      });
  }
}

@Component({
  selector: 'app-purchase-request-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ (data ? 'pr.edit' : 'pr.new') | t }}</h2>
    <mat-dialog-content>
      <div class="form-grid" [formGroup]="form">
        <mat-form-field>
          <mat-label>{{ 'app.date' | t }}</mat-label>
          <input matInput type="date" formControlName="requestDate" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'pr.required' | t }}</mat-label>
          <input matInput type="date" formControlName="requiredDate" dir="ltr" />
        </mat-form-field>
        <mat-form-field>
          <mat-label>{{ 'po.warehouse' | t }}</mat-label>
          <mat-select formControlName="warehouseId">
            @for (w of warehouses(); track w.id) { <mat-option [value]="w.id">{{ w | bi }}</mat-option> }
          </mat-select>
        </mat-form-field>
        <mat-form-field class="wide">
          <mat-label>{{ 'app.notes' | t }}</mat-label>
          <input matInput formControlName="notes" />
        </mat-form-field>
      </div>
      @for (l of lines.controls; track l; let i = $index) {
        <div class="line" [formGroup]="l">
          <mat-form-field class="item">
            <mat-label>{{ 'po.item' | t }}</mat-label>
            <mat-select formControlName="itemId">
              @for (it of items(); track it.id) { <mat-option [value]="it.id"><span class="ltr">{{ it.sku }}</span> · {{ it | bi }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <mat-form-field class="num-field">
            <mat-label>{{ 'po.qty' | t }}</mat-label>
            <input matInput type="number" min="0" formControlName="quantity" dir="ltr" />
          </mat-form-field>
          <mat-form-field class="num-field">
            <mat-label>{{ 'pr.estPrice' | t }}</mat-label>
            <input matInput type="number" min="0" formControlName="estimatedPrice" dir="ltr" />
          </mat-form-field>
          <button mat-icon-button (click)="lines.removeAt(i)" [disabled]="lines.length === 1"><mat-icon>delete</mat-icon></button>
        </div>
      }
      <div class="foot">
        <button mat-button (click)="addLine()"><mat-icon>add</mat-icon> {{ 'doc.addLine' | t }}</button>
        <span class="spacer"></span>
        <span>{{ 'pr.estimated' | t }}: <b class="ltr">{{ total() | money }}</b></span>
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button (click)="save()" [disabled]="form.invalid || lines.invalid || busy()">{{ 'app.save' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `
    .form-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); gap: 0 12px; }
    .wide { grid-column: 1 / -1; }
    .line { display: flex; gap: 8px; align-items: center; }
    .item { flex: 1; min-width: 0; }
    .num-field { width: 120px; }
    .foot { display: flex; align-items: center; gap: 8px; }
    .spacer { flex: 1; }
  `
})
export class PurchaseRequestDialog implements OnInit {
  protected readonly data = inject<PurchaseRequest | null>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<PurchaseRequestDialog, boolean>);
  private readonly api = inject(ApiService);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  private readonly fb = inject(FormBuilder).nonNullable;
  protected readonly warehouses = signal<Master[]>([]);
  protected readonly items = signal<Item[]>([]);
  protected readonly busy = signal(false);
  protected readonly form = this.fb.group({
    requestDate: [todayIso(), Validators.required],
    requiredDate: [''],
    warehouseId: ['', Validators.required],
    notes: ['']
  });
  protected readonly lines = new FormArray<LineGroup>([]);
  private readonly version = signal(0);
  protected readonly total = computed(() => {
    this.version();
    return this.lines.controls.reduce((s, l) => s + Number(l.value.quantity ?? 0) * Number(l.value.estimatedPrice ?? 0), 0);
  });

  ngOnInit(): void {
    this.lines.valueChanges.subscribe(() => this.version.update((v) => v + 1));
    this.trade.warehouses().subscribe((w) => {
      this.warehouses.set(w.filter((x) => x.isActive));
      if (!this.form.value.warehouseId) this.form.patchValue({ warehouseId: w.find((x) => x.isActive)?.id ?? '' });
    });
    this.trade.items({ active: true, pageSize: 500 }).subscribe((r) => this.items.set(r.items));
    if (this.data) {
      this.form.patchValue({ requestDate: this.data.requestDate, requiredDate: this.data.requiredDate ?? '', warehouseId: this.data.warehouseId,
        notes: this.data.notes ?? '' });
      for (const l of this.data.lines) this.addLine(l.itemId, l.quantity, l.estimatedPrice, l.description ?? '');
    } else {
      this.addLine();
    }
  }

  protected addLine(itemId = '', quantity = 1, estimatedPrice = 0, description = ''): void {
    this.lines.push(this.fb.group({
      itemId: [itemId, Validators.required], quantity: [quantity, [Validators.required, Validators.min(0.000001)]],
      estimatedPrice: [estimatedPrice, [Validators.required, Validators.min(0)]], description: [description]
    }));
  }

  protected save(): void {
    const v = this.form.getRawValue();
    const body = {
      requestDate: v.requestDate, requiredDate: v.requiredDate || null, warehouseId: v.warehouseId, notes: v.notes || null,
      lines: this.lines.getRawValue().map((l) => ({ itemId: l.itemId, quantity: Number(l.quantity), estimatedPrice: Number(l.estimatedPrice),
        description: l.description || null }))
    };
    this.busy.set(true);
    const call = this.data ? this.api.put(`purchasing/requests/${this.data.id}`, body) : this.api.post('purchasing/requests', body);
    call.subscribe({
      next: () => {
        this.notify.success();
        this.ref.close(true);
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }
}

@Component({
  selector: 'app-convert-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'pr.convert' | t }}</h2>
    <mat-dialog-content>
      <p class="secondary">{{ 'pr.convertHint' | t }}</p>
      <mat-form-field class="full">
        <mat-label>{{ 'po.supplier' | t }}</mat-label>
        <mat-select [formControl]="supplier">
          @for (s of suppliers(); track s.id) { <mat-option [value]="s.id">{{ s | bi }}</mat-option> }
        </mat-select>
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="!supplier.value" (click)="ref.close(supplier.value)">{{ 'pr.convert' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `.full { width: 100%; } .secondary { color: var(--mat-sys-on-surface-variant); }`
})
export class ConvertDialog implements OnInit {
  protected readonly ref = inject(MatDialogRef<ConvertDialog, string>);
  private readonly trade = inject(TradeApi);
  protected readonly suppliers = signal<Supplier[]>([]);
  protected readonly supplier = new FormControl('', { nonNullable: true });

  ngOnInit(): void {
    this.trade.suppliers().subscribe((s) => this.suppliers.set(s.filter((x) => x.isActive)));
  }
}
