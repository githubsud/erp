import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { Router } from '@angular/router';
import { filter, switchMap } from 'rxjs';
import { AuthService } from '../../core/auth.service';
import { todayIso } from '../../core/dates';
import { NotifyService } from '../../core/notify.service';
import { TradeApi } from '../../core/trade.api';
import { Master, StockCount } from '../../core/trade.models';
import { ConfirmDialog } from '../../shared/dialogs';
import { SHARED } from '../../shared/shared';

@Component({
  selector: 'app-counts',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'cnt.title' | t }}</h1>
    </div>
    @if (auth.can('inv.stock:post')) {
      <mat-card appearance="outlined" class="form-card">
        <form [formGroup]="form" class="form-grid" (ngSubmit)="open()">
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
            <mat-label>{{ 'cnt.group' | t }}</mat-label>
            <mat-select formControlName="itemGroupId">
              <mat-option [value]="null">—</mat-option>
              @for (g of groups(); track g.id) { <mat-option [value]="g.id">{{ g | bi }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <div class="actions center"><button mat-flat-button [disabled]="form.invalid || busy()"><mat-icon>inventory</mat-icon> {{ 'cnt.new' | t }}</button></div>
        </form>
      </mat-card>
    }
    <div class="table-wrap">
      <table mat-table [dataSource]="rows()">
        <ng-container matColumnDef="no">
          <th mat-header-cell *matHeaderCellDef>{{ 'cnt.no' | t }}</th>
          <td mat-cell *matCellDef="let c"><span class="ltr">{{ c.countNo }}</span></td>
        </ng-container>
        <ng-container matColumnDef="date">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.date' | t }}</th>
          <td mat-cell *matCellDef="let c" class="ltr">{{ c.countDate | d }}</td>
        </ng-container>
        <ng-container matColumnDef="warehouse">
          <th mat-header-cell *matHeaderCellDef>{{ 'stock.warehouse' | t }}</th>
          <td mat-cell *matCellDef="let c">{{ warehouseName(c.warehouseId) }}</td>
        </ng-container>
        <ng-container matColumnDef="progress">
          <th mat-header-cell *matHeaderCellDef>{{ 'cnt.progress' | t }}</th>
          <td mat-cell *matCellDef="let c" class="ltr">{{ c.countedLines }} / {{ c.lineCount }}</td>
        </ng-container>
        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
          <td mat-cell *matCellDef="let c"><span class="chip" [class.ok]="c.status === 'Posted'" [class.warn]="c.status === 'Open'">{{ 'cnt.status.' + c.status | t }}</span></td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns" class="clickable" (click)="router.navigate(['/inventory/counts', row.id])"></tr>
      </table>
      @if (rows().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `,
  styles: `.center { align-items: center; }`
})
export class CountsPage {
  protected readonly auth = inject(AuthService);
  protected readonly router = inject(Router);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);

  protected readonly columns = ['no', 'date', 'warehouse', 'progress', 'status'];
  protected readonly rows = signal<StockCount[]>([]);
  protected readonly warehouses = signal<Master[]>([]);
  protected readonly groups = signal<Master[]>([]);
  protected readonly activeWarehouses = computed(() => this.warehouses().filter((w) => w.isActive));
  protected readonly busy = signal(false);
  protected readonly form = inject(FormBuilder).group({
    warehouseId: ['', Validators.required],
    date: [todayIso(), Validators.required],
    itemGroupId: [null as string | null]
  });

  constructor() {
    this.trade.warehouses().subscribe((w) => {
      this.warehouses.set(w);
      this.form.patchValue({ warehouseId: w.find((x) => x.isActive)?.id ?? '' });
    });
    this.trade.groups().subscribe((g) => this.groups.set(g));
    this.trade.counts().subscribe((c) => this.rows.set(c));
  }

  protected warehouseName(id: string): string {
    return this.warehouses().find((w) => w.id === id)?.code ?? '';
  }

  protected open(): void {
    this.busy.set(true);
    this.trade.openCount(this.form.getRawValue()).subscribe({
      next: (c) => void this.router.navigate(['/inventory/counts', c.id]),
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }
}

@Component({
  selector: 'app-count-detail',
  imports: [...SHARED],
  template: `
    @if (count(); as c) {
      <div class="page-header">
        <a mat-icon-button routerLink="/inventory/counts"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
        <h1>{{ 'cnt.title' | t }} <span class="ltr">{{ c.countNo }}</span></h1>
        <span class="chip" [class.ok]="c.status === 'Posted'" [class.warn]="c.status === 'Open'">{{ 'cnt.status.' + c.status | t }}</span>
      </div>
      @if (editable()) {
        <p class="secondary">{{ 'cnt.hint' | t }}</p>
        <div class="actions bar">
          <button mat-button (click)="fill()"><mat-icon>content_copy</mat-icon> {{ 'cnt.fill' | t }}</button>
          <span class="spacer"></span>
          <button mat-button (click)="cancel()" [disabled]="busy()">{{ 'cnt.cancel' | t }}</button>
          <button mat-stroked-button (click)="save()" [disabled]="busy()"><mat-icon>save</mat-icon> {{ 'cnt.save' | t }}</button>
          <button mat-flat-button (click)="post()" [disabled]="busy()"><mat-icon>task_alt</mat-icon> {{ 'cnt.post' | t }}</button>
        </div>
      }
      @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
      <div class="table-wrap">
        <table class="count">
          <thead>
            <tr><th>{{ 'item.name' | t }}</th><th class="num">{{ 'cnt.system' | t }}</th><th class="num">{{ 'cnt.counted' | t }}</th><th class="num">{{ 'cnt.variance' | t }}</th></tr>
          </thead>
          <tbody>
            @for (l of c.lines; track l.itemId) {
              <tr [class.diff]="variance(l.itemId, l.systemQuantity) !== 0">
                <td><span class="ltr">{{ l.sku }}</span> · {{ l | bi }} <span class="secondary">{{ l.uomCode }}</span></td>
                <td class="num ltr">{{ l.systemQuantity }}</td>
                <td class="num">
                  @if (editable()) {
                    <input class="qty" type="number" min="0" dir="ltr" [formControl]="control(l.itemId)" />
                  } @else {
                    <span class="ltr">{{ l.countedQuantity }}</span>
                  }
                </td>
                <td class="num ltr">{{ variance(l.itemId, l.systemQuantity) ?? '' }}</td>
              </tr>
            }
          </tbody>
        </table>
        @if (c.lines.length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
      </div>
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: `
    .bar { align-items: center; margin-block-end: 12px; }
    .spacer { flex: 1; }
    .count { border-collapse: collapse; }
    .count th, .count td { padding: 8px 12px; border-bottom: 1px solid var(--mat-sys-outline-variant); text-align: start; }
    .count .num { text-align: end; }
    .qty { width: 110px; padding: 6px 8px; border: 1px solid var(--mat-sys-outline); border-radius: 8px; font: inherit; text-align: end;
      background: var(--mat-sys-surface); color: inherit; }
    tr.diff td { background: #fff7e6; }
  `
})
export class CountDetailPage implements OnInit {
  readonly id = input.required<string>();
  protected readonly auth = inject(AuthService);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  private readonly dialog = inject(MatDialog);

  protected readonly count = signal<StockCount | null>(null);
  protected readonly busy = signal(false);
  protected readonly editable = computed(() => this.count()?.status === 'Open' && this.auth.can('inv.stock:post'));
  private readonly controls = new Map<string, FormControl<number | null>>();
  private readonly values = signal(new Map<string, number | null>());

  ngOnInit(): void {
    this.trade.count(this.id()).subscribe({ next: (c) => this.set(c), error: (e) => this.notify.error(e) });
  }

  protected control(itemId: string): FormControl<number | null> {
    return this.controls.get(itemId)!;
  }

  protected variance(itemId: string, system: number): number | null {
    const v = this.values().get(itemId);
    return v === null || v === undefined || (v as unknown) === '' ? null : Number(v) - system;
  }

  protected fill(): void {
    for (const l of this.count()!.lines) {
      const c = this.controls.get(l.itemId)!;
      if (c.value === null || (c.value as unknown) === '') c.setValue(l.systemQuantity);
    }
  }

  protected save(after?: () => void): void {
    this.busy.set(true);
    const lines = this.count()!.lines.map((l) => {
      const v = this.controls.get(l.itemId)!.value;
      return { itemId: l.itemId, countedQuantity: v === null || (v as unknown) === '' ? null : Number(v) };
    });
    this.trade.enterCounts(this.id(), lines).subscribe({
      next: (c) => {
        this.busy.set(false);
        this.set(c);
        if (after) after();
        else this.notify.success();
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }

  protected post(): void {
    this.dialog
      .open(ConfirmDialog, { data: { title: 'cnt.post', message: 'cnt.hint' } })
      .afterClosed()
      .pipe(filter(Boolean))
      .subscribe(() => this.save(() => this.action('post')));
  }

  protected cancel(): void {
    this.dialog
      .open(ConfirmDialog, { data: { title: 'cnt.cancel', message: 'cnt.cancel' } })
      .afterClosed()
      .pipe(filter(Boolean), switchMap(() => this.trade.countAction(this.id(), 'cancel')))
      .subscribe({ next: (c) => this.set(c), error: (e) => this.notify.error(e) });
  }

  private action(action: 'post' | 'cancel'): void {
    this.busy.set(true);
    this.trade.countAction(this.id(), action).subscribe({
      next: (c) => {
        this.busy.set(false);
        this.set(c);
        this.notify.success();
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }

  private set(c: StockCount): void {
    this.count.set(c);
    const values = new Map<string, number | null>();
    for (const l of c.lines) {
      let control = this.controls.get(l.itemId);
      if (!control) {
        control = new FormControl<number | null>(l.countedQuantity);
        control.valueChanges.subscribe((v) => this.values.update((m) => new Map(m).set(l.itemId, v)));
        this.controls.set(l.itemId, control);
      } else {
        control.setValue(l.countedQuantity, { emitEvent: false });
      }
      values.set(l.itemId, l.countedQuantity);
    }
    this.values.set(values);
  }
}
