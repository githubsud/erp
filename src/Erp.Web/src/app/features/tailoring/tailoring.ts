import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { AiService } from '../../core/ai.service';
import { AiDraftDialog } from '../ai/draft-dialog';
import { catchError, filter, of } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { addDaysIso, todayIso } from '../../core/dates';
import { I18nService } from '../../core/i18n/i18n.service';
import { Account } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { ReceiptService } from '../../core/receipt.service';
import { TradeApi } from '../../core/trade.api';
import { Master } from '../../core/trade.models';
import { SHARED } from '../../shared/shared';

interface TLine {
  lineNo: number;
  itemId: string | null;
  description: string;
  quantity: number;
  unitPrice: number;
  amount: number;
}

interface TOrder {
  id: string;
  orderNo: string;
  customerId: string;
  customerName: string;
  phone: string | null;
  whatsAppPhone: string | null;
  orderDate: string;
  fittingDate: string | null;
  dueDate: string;
  garment: string;
  measurements: Record<string, string>;
  styleNotes: string | null;
  tailorName: string | null;
  status: 'Received' | 'InProgress' | 'Fitting' | 'Ready' | 'Delivered' | 'Cancelled';
  total: number;
  paid: number;
  balance: number;
  readyOn: string | null;
  deliveredOn: string | null;
  salesInvoiceId: string | null;
  late: boolean;
  lines: TLine[];
}

interface Board {
  open: number;
  dueToday: number;
  late: number;
  readyNotCollected: number;
  depositsHeld: number;
  tailors: { tailor: string; received: number; inProgress: number; fitting: number; dueThisWeek: number; late: number }[];
}

/** Measurement fields asked for each garment (stored as name → value). */
export const MEASUREMENTS: Record<string, string[]> = {
  Abaya: ['length', 'shoulder', 'chest', 'waist', 'hip', 'sleeveLength', 'sleeveWidth', 'armHole'],
  Dress: ['length', 'shoulder', 'bust', 'waist', 'hip', 'sleeveLength', 'armHole'],
  Thobe: ['length', 'shoulder', 'chest', 'sleeveLength', 'neck', 'cuff'],
  Jalabiya: ['length', 'shoulder', 'chest', 'sleeveLength'],
  Other: ['length', 'shoulder', 'chest', 'waist']
};

const TONE: Record<string, string> = { Received: 'info', InProgress: 'info', Fitting: 'warn', Ready: 'ok', Delivered: '', Cancelled: 'bad' };
const NEXT: Record<string, string | null> = { Received: 'InProgress', InProgress: 'Fitting', Fitting: 'Ready', Ready: null };

@Component({
  selector: 'app-tailoring',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'tl.title' | t }}</h1>
      <span class="spacer"></span>
      <button mat-flat-button (click)="create()"><mat-icon>add</mat-icon> {{ 'tl.new' | t }}</button>
    </div>
    @if (board(); as b) {
      <div class="card-grid">
        <mat-card appearance="outlined" class="stat" (click)="setFilter('open')"><div class="label">{{ 'tl.open' | t }}</div><div class="value">{{ b.open }}</div></mat-card>
        <mat-card appearance="outlined" class="stat"><div class="label">{{ 'tl.dueToday' | t }}</div><div class="value">{{ b.dueToday }}</div></mat-card>
        <mat-card appearance="outlined" class="stat" [class.bad]="b.late > 0"><div class="label">{{ 'tl.late' | t }}</div><div class="value">{{ b.late }}</div></mat-card>
        <mat-card appearance="outlined" class="stat" (click)="setFilter('Ready')"><div class="label">{{ 'tl.readyNotCollected' | t }}</div><div class="value">{{ b.readyNotCollected }}</div></mat-card>
        <mat-card appearance="outlined" class="stat"><div class="label">{{ 'tl.deposits' | t }}</div><div class="value ltr">{{ b.depositsHeld | money }}</div></mat-card>
      </div>
      @if (b.tailors.length) {
        <div class="tailors">
          @for (x of b.tailors; track x.tailor) {
            <span class="chip" [class.bad]="x.late > 0">{{ x.tailor }} · {{ x.received + x.inProgress + x.fitting }} @if (x.late) { · {{ 'tl.late' | t }} {{ x.late }} }</span>
          }
        </div>
      }
    }
    <div class="toolbar">
      <mat-form-field><mat-label>{{ 'app.status' | t }}</mat-label>
        <mat-select [formControl]="status" (selectionChange)="load()">
          <mat-option value="open">{{ 'tl.open' | t }}</mat-option>
          @for (s of statuses; track s) { <mat-option [value]="s">{{ 'tl.status.' + s | t }}</mat-option> }
        </mat-select></mat-form-field>
      <mat-form-field><mat-label>{{ 'app.search' | t }}</mat-label><input matInput [formControl]="search" (keydown.enter)="load()" /></mat-form-field>
    </div>
    <div class="table-wrap">
      <table class="line-editor">
        <thead><tr><th>{{ 'tl.order' | t }}</th><th>{{ 'tl.customer' | t }}</th><th>{{ 'tl.garment' | t }}</th><th>{{ 'tl.tailor' | t }}</th><th>{{ 'tl.due' | t }}</th>
          <th class="num">{{ 'app.total' | t }}</th><th class="num">{{ 'tl.balance' | t }}</th><th>{{ 'app.status' | t }}</th></tr></thead>
        <tbody>
          @for (o of orders(); track o.id) {
            <tr class="clickable" (click)="open(o)">
              <td class="ltr">{{ o.orderNo }}</td><td>{{ o.customerName }} <div class="secondary ltr">{{ o.phone }}</div></td>
              <td>{{ 'tl.g.' + o.garment | t }}</td><td>{{ o.tailorName }}</td>
              <td class="ltr" [class.bad]="o.late">{{ o.dueDate | d }}</td>
              <td class="num ltr">{{ o.total | money }}</td><td class="num ltr">{{ o.balance | money }}</td>
              <td><span class="chip" [class]="tone(o.status)">{{ 'tl.status.' + o.status | t }}</span> @if (o.late) { <span class="chip bad">{{ 'tl.late' | t }}</span> }</td>
            </tr>
          }
        </tbody>
      </table>
      @if (!orders().length) { <p class="secondary">{{ 'app.none' | t }}</p> }
    </div>
  `,
  styles: `
    .spacer { flex: 1; }
    .stat { cursor: pointer; }
    .stat.bad .value { color: #8c1d18; }
    .tailors { display: flex; flex-wrap: wrap; gap: 6px; margin-block: 8px; }
    .toolbar { display: flex; flex-wrap: wrap; gap: 8px; align-items: baseline; margin-block-start: 12px; }
    .clickable { cursor: pointer; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 12px; }
    .bad { color: #8c1d18; }
  `
})
export class TailoringPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  protected readonly statuses = ['Received', 'InProgress', 'Fitting', 'Ready', 'Delivered', 'Cancelled'];
  protected readonly board = signal<Board | null>(null);
  protected readonly orders = signal<TOrder[]>([]);
  protected readonly status = new FormControl('open', { nonNullable: true });
  protected readonly search = new FormControl('', { nonNullable: true });
  protected readonly tone = (s: string) => TONE[s] ?? '';

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.api.get<Board>('tailoring/board').subscribe((b) => this.board.set(b));
    this.api.get<TOrder[]>('tailoring/orders', { status: this.status.value, search: this.search.value }).subscribe({
      next: (o) => this.orders.set(o),
      error: (e) => this.notify.error(e)
    });
  }

  protected setFilter(s: string): void {
    this.status.setValue(s);
    this.load();
  }

  protected create(): void {
    this.dialog.open(TailoringOrderDialog, { width: '820px', maxHeight: '92vh' }).afterClosed().pipe(filter(Boolean)).subscribe((o: TOrder) => {
      this.load();
      this.open(o);
    });
  }

  protected open(o: TOrder): void {
    this.dialog.open(TailoringDetailDialog, { data: o, width: '720px', maxHeight: '92vh' }).afterClosed().subscribe(() => this.load());
  }
}

/** Shared by the order and detail dialogs: cash/bank accounts to take money into. */
function moneyAccounts(api: ApiService, set: (a: Account[]) => void, pick: (id: string) => void): void {
  api.accounts().subscribe((a) => {
    const list = a.filter((x) => x.isPostable && x.isActive && (x.subtype === 'cash' || x.subtype === 'bank'));
    set(list);
    pick(list.find((x) => x.code === '1110')?.id ?? list[0]?.id ?? '');
  });
}

@Component({
  selector: 'app-tailoring-order',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'tl.new' | t }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field><mat-label>{{ 'tl.customerName' | t }}</mat-label><input matInput formControlName="customerName" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'tl.phone' | t }}</mat-label><input matInput formControlName="phone" dir="ltr" (blur)="lookup()" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'tl.garment' | t }}</mat-label>
          <mat-select formControlName="garment" (selectionChange)="garmentChanged()">@for (g of garments; track g) { <mat-option [value]="g">{{ 'tl.g.' + g | t }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>{{ 'tl.tailor' | t }}</mat-label><input matInput formControlName="tailorName" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'tl.fitting' | t }}</mat-label><input matInput type="date" formControlName="fittingDate" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'tl.due' | t }}</mat-label><input matInput type="date" formControlName="dueDate" /></mat-form-field>
        <mat-form-field class="wide"><mat-label>{{ 'tl.style' | t }}</mat-label><input matInput formControlName="styleNotes" /></mat-form-field>
      </form>
      <h3>{{ 'tl.measurements' | t }} <span class="secondary">({{ 'tl.cm' | t }})</span> @if (remembered()) { <span class="chip ok">{{ 'tl.remembered' | t }}</span> }</h3>
      <div class="measure">
        @for (m of fields(); track m) {
          <mat-form-field><mat-label>{{ 'tl.m.' + m | t }}</mat-label><input matInput dir="ltr" [formControl]="measure(m)" /></mat-form-field>
        }
      </div>
      <h3>{{ 'tl.items' | t }}</h3>
      <table class="line-editor">
        <thead><tr><th>{{ 'tl.description' | t }}</th><th class="narrow">{{ 'po.qty' | t }}</th><th class="narrow">{{ 'tl.price' | t }}</th><th class="num">{{ 'app.amount' | t }}</th><th></th></tr></thead>
        <tbody>
          @for (l of lines.controls; track l; let i = $index) {
            <tr [formGroup]="l">
              <td><mat-form-field><input matInput formControlName="description" /></mat-form-field></td>
              <td class="narrow"><mat-form-field><input matInput type="number" formControlName="quantity" dir="ltr" /></mat-form-field></td>
              <td class="narrow"><mat-form-field><input matInput type="number" formControlName="unitPrice" dir="ltr" /></mat-form-field></td>
              <td class="num ltr">{{ (l.value.quantity || 0) * (l.value.unitPrice || 0) | money }}</td>
              <td><button mat-icon-button (click)="lines.removeAt(i)" [disabled]="lines.length === 1"><mat-icon>delete</mat-icon></button></td>
            </tr>
          }
        </tbody>
      </table>
      <div class="row">
        <button mat-button (click)="addLine()"><mat-icon>add</mat-icon> {{ 'doc.addLine' | t }}</button>
        <span class="spacer"></span><b>{{ 'app.total' | t }} <span class="ltr">{{ total() | money }}</span></b>
      </div>
      <h3>{{ 'tl.deposit' | t }}</h3>
      <div class="form-grid">
        <mat-form-field><mat-label>{{ 'app.amount' | t }}</mat-label><input matInput type="number" [formControl]="deposit" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'tl.into' | t }}</mat-label>
          <mat-select [formControl]="account">@for (a of accounts(); track a.id) { <mat-option [value]="a.id"><span class="ltr">{{ a.code }}</span> {{ a | bi }}</mat-option> }</mat-select></mat-form-field>
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid || lines.invalid || busy()" (click)="save()">{{ 'app.save' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `
    .measure { display: grid; grid-template-columns: repeat(auto-fill, minmax(130px, 1fr)); gap: 0 8px; }
    .row { display: flex; align-items: center; } .spacer { flex: 1; } .wide { grid-column: 1 / -1; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 12px; font-weight: normal; }
  `
})
export class TailoringOrderDialog implements OnInit {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  private readonly ref = inject(MatDialogRef<TailoringOrderDialog, TOrder>);
  private readonly fb = inject(FormBuilder).nonNullable;
  protected readonly garments = Object.keys(MEASUREMENTS);
  protected readonly accounts = signal<Account[]>([]);
  protected readonly busy = signal(false);
  protected readonly remembered = signal(false);
  private readonly values = new Map<string, FormControl<string>>();
  private readonly version = signal(0);
  protected readonly form = this.fb.group({
    customerName: ['', Validators.required], phone: ['', [Validators.required, Validators.minLength(8)]], garment: ['Abaya'], tailorName: [''],
    fittingDate: [addDaysIso(todayIso(), 5)], dueDate: [addDaysIso(todayIso(), 10), Validators.required], styleNotes: ['']
  });
  protected readonly fields = signal(MEASUREMENTS['Abaya']);
  protected readonly lines = this.fb.array([this.line()]);
  protected readonly deposit = new FormControl<number | null>(null);
  protected readonly account = new FormControl('', { nonNullable: true });
  private customerId: string | null = null;
  protected readonly total = computed(() => {
    this.version();
    return this.lines.getRawValue().reduce((s, l) => s + (Number(l.quantity) || 0) * (Number(l.unitPrice) || 0), 0);
  });

  ngOnInit(): void {
    this.lines.valueChanges.subscribe(() => this.version.update((v) => v + 1));
    moneyAccounts(this.api, (a) => this.accounts.set(a), (id) => this.account.setValue(id));
  }

  private line(description = '', quantity = 1, unitPrice = 0) {
    return this.fb.group({ description: [description, Validators.required], quantity: [quantity, Validators.min(0.01)], unitPrice: [unitPrice, Validators.min(0)] });
  }

  protected addLine(): void {
    this.lines.push(this.line());
  }

  protected measure(name: string): FormControl<string> {
    if (!this.values.has(name)) this.values.set(name, new FormControl('', { nonNullable: true }));
    return this.values.get(name)!;
  }

  protected garmentChanged(): void {
    this.fields.set(MEASUREMENTS[this.form.value.garment ?? 'Other']);
    this.lookup();
  }

  /** Known customer (by phone): fill the name and the measurements kept from earlier orders. */
  protected lookup(): void {
    const phone = (this.form.value.phone ?? '').replace(/\D/g, '');
    if (phone.length < 8) return;
    this.api.get<{ items: { id: string; nameEn: string; phone: string | null }[] } | { id: string; nameEn: string; phone: string | null }[]>('sales/customers', { search: phone.slice(-8) })
      .pipe(catchError(() => of([])))
      .subscribe((r) => {
        const list = Array.isArray(r) ? r : r.items;
        const c = list.find((x) => (x.phone ?? '').replace(/\D/g, '').endsWith(phone.slice(-8)));
        if (!c) {
          this.customerId = null;
          return;
        }
        this.customerId = c.id;
        if (!this.form.value.customerName) this.form.patchValue({ customerName: c.nameEn });
        this.api.get<Record<string, Record<string, string>>>(`tailoring/customers/${c.id}/measurements`).subscribe((p) => {
          const m = p[this.form.value.garment ?? 'Abaya'];
          this.remembered.set(!!m);
          if (m) for (const [k, v] of Object.entries(m)) this.measure(k).setValue(v);
        });
      });
  }

  protected save(): void {
    const v = this.form.getRawValue();
    const measurements = Object.fromEntries(this.fields().map((f) => [f, this.measure(f).value]).filter(([, x]) => x));
    this.busy.set(true);
    this.api.post<TOrder>('tailoring/orders', {
      customerId: this.customerId, customerName: v.customerName, phone: v.phone, orderDate: todayIso(), dueDate: v.dueDate, fittingDate: v.fittingDate || null,
      garment: v.garment, measurements, styleNotes: v.styleNotes || null, tailorName: v.tailorName || null,
      lines: this.lines.getRawValue().map((l) => ({ itemId: null, description: l.description, quantity: Number(l.quantity), unitPrice: Number(l.unitPrice) })),
      deposit: Number(this.deposit.value) > 0 ? { amount: Number(this.deposit.value), accountId: this.account.value, method: 'Cash' } : null
    }).subscribe({
      next: (o) => this.ref.close(o),
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }
}

@Component({
  selector: 'app-tailoring-detail',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title><span class="ltr">{{ o().orderNo }}</span> · {{ o().customerName }}
      <span class="chip" [class]="tone(o().status)">{{ 'tl.status.' + o().status | t }}</span></h2>
    <mat-dialog-content>
      <div class="facts">
        <div><span class="secondary">{{ 'tl.garment' | t }}</span> {{ 'tl.g.' + o().garment | t }}</div>
        <div><span class="secondary">{{ 'tl.tailor' | t }}</span> {{ o().tailorName || '—' }}</div>
        <div><span class="secondary">{{ 'tl.fitting' | t }}</span> <span class="ltr">{{ o().fittingDate | d }}</span></div>
        <div><span class="secondary">{{ 'tl.due' | t }}</span> <span class="ltr" [class.bad]="o().late">{{ o().dueDate | d }}</span></div>
        <div><span class="secondary">{{ 'tl.phone' | t }}</span> <span class="ltr">{{ o().phone }}</span></div>
      </div>
      @if (o().styleNotes) { <p>{{ o().styleNotes }}</p> }
      <div class="measure">
        @for (m of measureList(); track m[0]) { <span class="chip">{{ 'tl.m.' + m[0] | t }} <b class="ltr">{{ m[1] }}</b></span> }
      </div>
      <table class="line-editor">
        <tbody>
          @for (l of o().lines; track l.lineNo) { <tr><td>{{ l.description }}</td><td class="num ltr">{{ l.quantity }} × {{ l.unitPrice | money }}</td><td class="num ltr">{{ l.amount | money }}</td></tr> }
          <tr><td colspan="2"><b>{{ 'app.total' | t }}</b></td><td class="num ltr"><b>{{ o().total | money }}</b></td></tr>
          <tr><td colspan="2">{{ 'tl.paid' | t }}</td><td class="num ltr">{{ o().paid | money }}</td></tr>
          <tr><td colspan="2"><b>{{ 'tl.balance' | t }}</b></td><td class="num ltr"><b>{{ o().balance | money }}</b></td></tr>
        </tbody>
      </table>
      @if (open()) {
        <div class="form-grid">
          <mat-form-field><mat-label>{{ 'app.amount' | t }}</mat-label><input matInput type="number" [formControl]="amount" dir="ltr" /></mat-form-field>
          <mat-form-field><mat-label>{{ 'tl.into' | t }}</mat-label>
            <mat-select [formControl]="account">@for (a of accounts(); track a.id) { <mat-option [value]="a.id"><span class="ltr">{{ a.code }}</span> {{ a | bi }}</mat-option> }</mat-select></mat-form-field>
          @if (warehouses().length && hasStock()) {
            <mat-form-field><mat-label>{{ 'po.warehouse' | t }}</mat-label>
              <mat-select [formControl]="warehouse">@for (w of warehouses(); track w.id) { <mat-option [value]="w.id">{{ w | bi }}</mat-option> }</mat-select></mat-form-field>
          }
        </div>
      }
    </mat-dialog-content>
    <mat-dialog-actions>
      @if (o().whatsAppPhone) { <a mat-button [href]="whatsApp()" target="_blank" rel="noopener"><mat-icon>chat</mat-icon> WhatsApp</a> }
      @if (aiOn) { <button mat-button (click)="aiMessage()"><mat-icon>auto_awesome</mat-icon> {{ 'ai.message' | t }}</button> }
      <button mat-button (click)="print()"><mat-icon>print</mat-icon> {{ 'tl.receipt' | t }}</button>
      <span class="spacer"></span>
      @if (open()) {
        <button mat-button (click)="cancel()">{{ 'app.cancel' | t }}</button>
        <button mat-stroked-button [disabled]="!(amount.value! > 0)" (click)="pay()">{{ 'tl.addPayment' | t }}</button>
        @if (next(); as n) { <button mat-stroked-button (click)="advance(n)">{{ 'tl.to.' + n | t }}</button> }
        <button mat-flat-button (click)="deliver()">{{ 'tl.deliver' | t }}</button>
      } @else { <button mat-button mat-dialog-close>{{ 'app.close' | t }}</button> }
    </mat-dialog-actions>
  `,
  styles: `
    .facts { display: grid; grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); gap: 6px 12px; margin-block-end: 8px; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 12px; margin-inline-end: 4px; }
    .measure { display: flex; flex-wrap: wrap; gap: 6px; margin-block: 8px; }
    .bad { color: #8c1d18; } .spacer { flex: 1; }
  `
})
export class TailoringDetailDialog implements OnInit {
  private readonly api = inject(ApiService);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  private readonly i18n = inject(I18nService);
  private readonly receipt = inject(ReceiptService);
  protected readonly o = signal(inject<TOrder>(MAT_DIALOG_DATA));
  protected readonly accounts = signal<Account[]>([]);
  protected readonly warehouses = signal<Master[]>([]);
  protected readonly amount = new FormControl<number | null>(null);
  protected readonly account = new FormControl('', { nonNullable: true });
  protected readonly warehouse = new FormControl('', { nonNullable: true });
  protected readonly open = computed(() => !['Delivered', 'Cancelled'].includes(this.o().status));
  protected readonly next = computed(() => NEXT[this.o().status] ?? null);
  protected readonly measureList = computed(() => Object.entries(this.o().measurements));
  protected readonly hasStock = computed(() => this.o().lines.some((l) => l.itemId));
  protected readonly tone = (s: string) => TONE[s] ?? '';

  ngOnInit(): void {
    this.amount.setValue(this.o().balance || null);
    moneyAccounts(this.api, (a) => this.accounts.set(a), (id) => this.account.setValue(id));
    this.trade.warehouses().pipe(catchError(() => of([]))).subscribe((w) => {
      this.warehouses.set(w.filter((x) => x.isActive));
      this.warehouse.setValue(w.find((x) => x.code === 'MAIN')?.id ?? w[0]?.id ?? '');
    });
  }

  private done = (o: TOrder) => {
    this.o.set(o);
    this.amount.setValue(o.balance || null);
    this.notify.success();
  };

  protected advance(status: string): void {
    this.api.post<TOrder>(`tailoring/orders/${this.o().id}/status`, { status }).subscribe({ next: this.done, error: (e) => this.notify.error(e) });
  }

  protected pay(): void {
    this.api.post<TOrder>(`tailoring/orders/${this.o().id}/payments`, { amount: Number(this.amount.value), accountId: this.account.value, method: 'Cash' })
      .subscribe({ next: this.done, error: (e) => this.notify.error(e) });
  }

  protected deliver(): void {
    const amount = Number(this.amount.value) || 0;
    if (!confirm(this.i18n.t('tl.deliverConfirm'))) return;
    this.api.post<TOrder>(`tailoring/orders/${this.o().id}/deliver`, {
      date: todayIso(), warehouseId: this.hasStock() ? this.warehouse.value || null : null,
      finalPayment: amount > 0 ? { amount, accountId: this.account.value, method: 'Cash' } : null
    }).subscribe({ next: this.done, error: (e) => this.notify.error(e) });
  }

  protected cancel(): void {
    if (!confirm(this.i18n.t('tl.cancelConfirm'))) return;
    this.api.post<TOrder>(`tailoring/orders/${this.o().id}/cancel`, {}).subscribe({ next: this.done, error: (e) => this.notify.error(e) });
  }

  protected readonly aiOn = inject(AiService).enabled();
  private readonly dialogs = inject(MatDialog);

  protected aiMessage(): void {
    const o = this.o();
    AiDraftDialog.open(this.dialogs, {
      kind: o.status === 'Ready' ? 'order_ready' : 'thank_you',
      phone: o.whatsAppPhone,
      facts: { customer: o.customerName, order_no: o.orderNo, balance_to_pay: o.balance.toFixed(2), due_date: o.dueDate, status: o.status }
    });
  }

  protected whatsApp(): string {
    const o = this.o();
    const text = o.status === 'Ready'
      ? this.i18n.t('tl.msg.ready', { name: o.customerName, no: o.orderNo, balance: o.balance.toFixed(2) })
      : this.i18n.t('tl.msg.received', { name: o.customerName, no: o.orderNo, due: o.dueDate.split('-').reverse().join('/') });
    return `https://wa.me/${o.whatsAppPhone}?text=${encodeURIComponent(text)}`;
  }

  protected print(): void {
    const o = this.o();
    const t = (k: Parameters<I18nService['t']>[0]) => this.i18n.t(k);
    const measures = Object.entries(o.measurements).map(([k, v]) => `${this.i18n.t('tl.m.' + k)}: ${v}`).join(' · ');
    this.receipt.print({
      title: t('tl.title'),
      number: o.orderNo,
      meta: [[o.customerName, o.phone].filter(Boolean).join(' · '), `${t('tl.due')}: ${o.dueDate.split('-').reverse().join('/')}`],
      lines: o.lines.map((l) => ({ text: l.description, amount: l.amount })),
      totals: [
        { label: t('app.total'), amount: o.total, bold: true },
        { label: t('tl.paid'), amount: o.paid },
        { label: t('tl.balance'), amount: o.balance, bold: true }
      ],
      note: measures || undefined,
      footer: t('tl.keepReceipt')
    });
  }
}
