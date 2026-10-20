import { DecimalPipe } from '@angular/common';
import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { catchError, filter, of } from 'rxjs';
import { AiService } from '../../core/ai.service';
import { ApiService } from '../../core/api.service';
import { addDaysIso, todayIso } from '../../core/dates';
import { I18nService } from '../../core/i18n/i18n.service';
import { Account } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { ReceiptService } from '../../core/receipt.service';
import { TradeApi } from '../../core/trade.api';
import { Master } from '../../core/trade.models';
import { SHARED } from '../../shared/shared';
import { AiDraftDialog } from '../ai/draft-dialog';

type Status = 'Received' | 'Diagnosing' | 'AwaitingApproval' | 'InProgress' | 'WaitingParts' | 'Ready' | 'Delivered' | 'Cancelled';

interface JobLine {
  lineNo: number;
  kind: 'Labour' | 'Part';
  itemId: string | null;
  description: string;
  quantity: number;
  unitPrice: number;
  amount: number;
}

interface Job {
  id: string;
  jobNo: string;
  customerId: string;
  customerName: string;
  phone: string | null;
  whatsAppPhone: string | null;
  vehicleId: string;
  plateNo: string;
  vehicle: string | null;
  receivedOn: string;
  promisedOn: string | null;
  mileage: number | null;
  complaint: string;
  diagnosis: string | null;
  technicianName: string | null;
  status: Status;
  approvedOn: string | null;
  total: number;
  paid: number;
  balance: number;
  readyOn: string | null;
  deliveredOn: string | null;
  salesInvoiceId: string | null;
  late: boolean;
  lines: JobLine[];
}

interface Board {
  open: number;
  awaitingApproval: number;
  waitingParts: number;
  late: number;
  readyNotCollected: number;
  depositsHeld: number;
  technicians: { technician: string; open: number; inProgress: number; late: number }[];
}

interface VehicleRow {
  id: string;
  plateNo: string;
  make: string | null;
  model: string | null;
  year: number | null;
  color: string | null;
  lastMileage: number | null;
  customerName: string;
  phone: string | null;
  visits: number;
  lastVisit: string | null;
}

interface CatalogItem {
  id: string;
  sku: string;
  nameEn: string;
  nameAr: string;
  type: string;
  salesPrice: number;
}

const STATUSES: Status[] = ['Received', 'Diagnosing', 'AwaitingApproval', 'InProgress', 'WaitingParts', 'Ready', 'Delivered', 'Cancelled'];
const TONE: Record<string, string> = {
  Received: 'info', Diagnosing: 'info', AwaitingApproval: 'warn', InProgress: 'info', WaitingParts: 'warn', Ready: 'ok', Delivered: '', Cancelled: 'bad'
};
/** The usual next steps from each status (any open status can still be chosen from the menu). */
const NEXT: Record<string, Status[]> = {
  Received: ['Diagnosing', 'AwaitingApproval'], Diagnosing: ['AwaitingApproval', 'InProgress'], AwaitingApproval: ['InProgress'],
  InProgress: ['WaitingParts', 'Ready'], WaitingParts: ['InProgress'], Ready: []
};

function moneyAccounts(api: ApiService, set: (a: Account[]) => void, pick: (id: string) => void): void {
  api.accounts().subscribe((a) => {
    const list = a.filter((x) => x.isPostable && x.isActive && (x.subtype === 'cash' || x.subtype === 'bank'));
    set(list);
    pick(list.find((x) => x.code === '1110')?.id ?? list[0]?.id ?? '');
  });
}

/** Car workshop: cars in the workshop, job cards with an estimate of parts and labour, and the history of each car by plate. */
@Component({
  selector: 'app-workshop',
  imports: [...SHARED, DecimalPipe],
  template: `
    <div class="page-header">
      <h1>{{ 'ws.title' | t }}</h1>
      <span class="spacer"></span>
      <button mat-flat-button (click)="create()"><mat-icon>add</mat-icon> {{ 'ws.new' | t }}</button>
    </div>
    @if (board(); as b) {
      <div class="card-grid">
        <mat-card appearance="outlined" class="stat" (click)="setFilter('open')"><div class="label">{{ 'ws.carsIn' | t }}</div><div class="value">{{ b.open }}</div></mat-card>
        <mat-card appearance="outlined" class="stat" (click)="setFilter('AwaitingApproval')"><div class="label">{{ 'ws.status.AwaitingApproval' | t }}</div><div class="value">{{ b.awaitingApproval }}</div></mat-card>
        <mat-card appearance="outlined" class="stat" (click)="setFilter('WaitingParts')"><div class="label">{{ 'ws.status.WaitingParts' | t }}</div><div class="value">{{ b.waitingParts }}</div></mat-card>
        <mat-card appearance="outlined" class="stat" [class.bad]="b.late > 0"><div class="label">{{ 'ws.late' | t }}</div><div class="value">{{ b.late }}</div></mat-card>
        <mat-card appearance="outlined" class="stat" (click)="setFilter('Ready')"><div class="label">{{ 'ws.readyNotCollected' | t }}</div><div class="value">{{ b.readyNotCollected }}</div></mat-card>
        <mat-card appearance="outlined" class="stat"><div class="label">{{ 'ws.advances' | t }}</div><div class="value ltr">{{ b.depositsHeld | money }}</div></mat-card>
      </div>
      @if (b.technicians.length) {
        <div class="chips">
          @for (x of b.technicians; track x.technician) {
            <span class="chip" [class.bad]="x.late > 0">{{ x.technician }} · {{ x.open }} @if (x.late) { · {{ 'ws.late' | t }} {{ x.late }} }</span>
          }
        </div>
      }
    }
    <mat-tab-group mat-stretch-tabs="false">
      <mat-tab [label]="'ws.jobs' | t">
        <div class="toolbar">
          <mat-form-field><mat-label>{{ 'app.status' | t }}</mat-label>
            <mat-select [formControl]="status" (selectionChange)="load()">
              <mat-option value="open">{{ 'ws.carsIn' | t }}</mat-option>
              <mat-option value="all">{{ 'ws.all' | t }}</mat-option>
              @for (s of statuses; track s) { <mat-option [value]="s">{{ 'ws.status.' + s | t }}</mat-option> }
            </mat-select></mat-form-field>
          <mat-form-field><mat-label>{{ 'ws.searchHint' | t }}</mat-label><input matInput [formControl]="search" (keydown.enter)="load()" /></mat-form-field>
        </div>
        <div class="table-wrap">
          <table class="line-editor">
            <thead><tr><th>{{ 'ws.job' | t }}</th><th>{{ 'ws.plate' | t }}</th><th>{{ 'ws.customer' | t }}</th><th>{{ 'ws.complaint' | t }}</th>
              <th>{{ 'ws.technician' | t }}</th><th>{{ 'ws.promised' | t }}</th><th class="num">{{ 'app.total' | t }}</th><th>{{ 'app.status' | t }}</th></tr></thead>
            <tbody>
              @for (j of jobs(); track j.id) {
                <tr class="clickable" (click)="open(j)">
                  <td class="ltr nowrap">{{ j.jobNo }}</td>
                  <td><b class="ltr plate">{{ j.plateNo }}</b><div class="secondary">{{ j.vehicle }}</div></td>
                  <td>{{ j.customerName }}<div class="secondary ltr">{{ j.phone }}</div></td>
                  <td class="complaint">{{ j.complaint }}</td>
                  <td>{{ j.technicianName }}</td>
                  <td class="ltr" [class.bad]="j.late">{{ j.promisedOn | d }}</td>
                  <td class="num ltr">{{ j.total | money }}</td>
                  <td><span class="chip" [class]="tone(j.status)">{{ 'ws.status.' + j.status | t }}</span> @if (j.late) { <span class="chip bad">{{ 'ws.late' | t }}</span> }</td>
                </tr>
              }
            </tbody>
          </table>
          @if (!jobs().length) { <p class="secondary">{{ 'app.none' | t }}</p> }
        </div>
      </mat-tab>
      <mat-tab [label]="'ws.cars' | t">
        <div class="toolbar">
          <mat-form-field><mat-label>{{ 'ws.searchHint' | t }}</mat-label><input matInput [formControl]="carSearch" (keydown.enter)="loadCars()" /></mat-form-field>
          <button mat-button (click)="loadCars()"><mat-icon>search</mat-icon> {{ 'app.search' | t }}</button>
        </div>
        <div class="table-wrap">
          <table class="line-editor">
            <thead><tr><th>{{ 'ws.plate' | t }}</th><th>{{ 'ws.car' | t }}</th><th>{{ 'ws.customer' | t }}</th><th class="num">{{ 'ws.mileage' | t }}</th>
              <th class="num">{{ 'ws.visits' | t }}</th><th>{{ 'ws.lastVisit' | t }}</th></tr></thead>
            <tbody>
              @for (v of cars(); track v.id) {
                <tr class="clickable" (click)="history(v)">
                  <td><b class="ltr plate">{{ v.plateNo }}</b></td><td>{{ [v.make, v.model, v.year, v.color].filter(isSet).join(' ') }}</td>
                  <td>{{ v.customerName }}<div class="secondary ltr">{{ v.phone }}</div></td>
                  <td class="num ltr">{{ v.lastMileage | number }}</td><td class="num ltr">{{ v.visits }}</td><td class="ltr">{{ v.lastVisit | d }}</td>
                </tr>
              }
            </tbody>
          </table>
          @if (!cars().length) { <p class="secondary">{{ 'app.none' | t }}</p> }
        </div>
      </mat-tab>
    </mat-tab-group>
  `,
  styles: `
    .spacer { flex: 1; }
    .stat { cursor: pointer; }
    .stat.bad .value { color: #8c1d18; }
    .chips { display: flex; flex-wrap: wrap; gap: 6px; margin-block: 8px; }
    .toolbar { display: flex; flex-wrap: wrap; gap: 8px; align-items: baseline; margin-block-start: 12px; }
    .clickable { cursor: pointer; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 12px; }
    .bad { color: #8c1d18; }
    .plate { letter-spacing: 1px; }
    .complaint { max-width: 260px; }
    .nowrap { white-space: nowrap; }
  `
})
export class WorkshopPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  protected readonly statuses = STATUSES;
  protected readonly board = signal<Board | null>(null);
  protected readonly jobs = signal<Job[]>([]);
  protected readonly cars = signal<VehicleRow[]>([]);
  protected readonly status = new FormControl('open', { nonNullable: true });
  protected readonly search = new FormControl('', { nonNullable: true });
  protected readonly carSearch = new FormControl('', { nonNullable: true });
  protected readonly tone = (s: string) => TONE[s] ?? '';
  protected readonly isSet = (x: unknown) => x !== null && x !== undefined && x !== '';

  ngOnInit(): void {
    this.load();
    this.loadCars();
  }

  protected load(): void {
    this.api.get<Board>('workshop/board').subscribe((b) => this.board.set(b));
    this.api.get<Job[]>('workshop/jobs', { status: this.status.value, search: this.search.value }).subscribe({
      next: (j) => this.jobs.set(j),
      error: (e) => this.notify.error(e)
    });
  }

  protected loadCars(): void {
    this.api.get<VehicleRow[]>('workshop/vehicles', { search: this.carSearch.value }).subscribe((v) => this.cars.set(v));
  }

  protected setFilter(s: string): void {
    this.status.setValue(s);
    this.load();
  }

  protected history(v: VehicleRow): void {
    this.status.setValue('all');
    this.search.setValue(v.plateNo);
    this.load();
  }

  protected create(): void {
    this.dialog.open(NewJobDialog, { width: '760px', maxHeight: '92vh' }).afterClosed().pipe(filter(Boolean)).subscribe((j: Job) => {
      this.load();
      this.loadCars();
      this.open(j);
    });
  }

  protected open(j: Job): void {
    this.dialog.open(JobDialog, { data: j, width: '860px', maxWidth: '96vw', maxHeight: '94vh' }).afterClosed().subscribe(() => this.load());
  }
}

/** A car comes in: the plate finds a known car and its owner; otherwise the walk-in's name and phone. */
@Component({
  selector: 'app-workshop-new-job',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'ws.new' | t }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field><mat-label>{{ 'ws.plate' | t }}</mat-label><input matInput formControlName="plateNo" dir="ltr" (blur)="lookup()" /></mat-form-field>
        @if (known(); as k) {
          <div class="known"><mat-icon>directions_car</mat-icon> {{ 'ws.knownCar' | t }}: <b>{{ k.customerName }}</b> · {{ [k.make, k.model, k.year].filter(isSet).join(' ') }}
            · {{ 'ws.visits' | t }} <span class="ltr">{{ k.visits }}</span></div>
        } @else {
          <mat-form-field><mat-label>{{ 'ws.customerName' | t }}</mat-label><input matInput formControlName="customerName" /></mat-form-field>
          <mat-form-field><mat-label>{{ 'ws.phone' | t }}</mat-label><input matInput formControlName="phone" dir="ltr" /></mat-form-field>
          <mat-form-field><mat-label>{{ 'ws.make' | t }}</mat-label><input matInput formControlName="make" /></mat-form-field>
          <mat-form-field><mat-label>{{ 'ws.model' | t }}</mat-label><input matInput formControlName="model" /></mat-form-field>
          <mat-form-field><mat-label>{{ 'ws.year' | t }}</mat-label><input matInput type="number" formControlName="year" dir="ltr" /></mat-form-field>
          <mat-form-field><mat-label>{{ 'ws.color' | t }}</mat-label><input matInput formControlName="color" /></mat-form-field>
        }
        <mat-form-field><mat-label>{{ 'ws.mileage' | t }}</mat-label><input matInput type="number" formControlName="mileage" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'ws.technician' | t }}</mat-label><input matInput formControlName="technicianName" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'ws.promised' | t }}</mat-label><input matInput type="date" formControlName="promisedOn" /></mat-form-field>
        <mat-form-field class="wide"><mat-label>{{ 'ws.complaint' | t }}</mat-label><textarea matInput rows="2" formControlName="complaint"></textarea></mat-form-field>
      </form>
      <h3>{{ 'ws.advance' | t }}</h3>
      <div class="form-grid">
        <mat-form-field><mat-label>{{ 'app.amount' | t }}</mat-label><input matInput type="number" [formControl]="deposit" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'tl.into' | t }}</mat-label>
          <mat-select [formControl]="account">@for (a of accounts(); track a.id) { <mat-option [value]="a.id"><span class="ltr">{{ a.code }}</span> {{ a | bi }}</mat-option> }</mat-select></mat-form-field>
      </div>
      <p class="secondary">{{ 'ws.estimateLater' | t }}</p>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="!valid() || busy()" (click)="save()">{{ 'app.save' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `
    .wide { grid-column: 1 / -1; }
    .known { grid-column: 1 / -1; display: flex; align-items: center; gap: 6px; padding: 8px 12px; border-radius: 12px; background: var(--mat-sys-secondary-container); }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 12px; }
  `
})
export class NewJobDialog implements OnInit {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  private readonly ref = inject(MatDialogRef<NewJobDialog, Job>);
  protected readonly accounts = signal<Account[]>([]);
  protected readonly busy = signal(false);
  protected readonly known = signal<VehicleRow | null>(null);
  protected readonly isSet = (x: unknown) => x !== null && x !== undefined && x !== '';
  protected readonly form = inject(FormBuilder).nonNullable.group({
    plateNo: ['', Validators.required], customerName: [''], phone: [''], make: [''], model: [''], year: [null as number | null], color: [''],
    mileage: [null as number | null], technicianName: [''], promisedOn: [addDaysIso(todayIso(), 1)], complaint: ['', Validators.required]
  });
  protected readonly deposit = new FormControl<number | null>(null);
  protected readonly account = new FormControl('', { nonNullable: true });

  ngOnInit(): void {
    moneyAccounts(this.api, (a) => this.accounts.set(a), (id) => this.account.setValue(id));
  }

  protected valid(): boolean {
    const v = this.form.getRawValue();
    return this.form.valid && (!!this.known() || (!!v.customerName && v.phone.replace(/\D/g, '').length >= 8));
  }

  /** A plate seen before brings the car and its owner. */
  protected lookup(): void {
    const plate = this.form.value.plateNo?.replace(/[^0-9a-z]/gi, '').toUpperCase() ?? '';
    if (!plate) return;
    this.api.get<VehicleRow[]>('workshop/vehicles', { search: plate }).pipe(catchError(() => of([]))).subscribe((v) => {
      const car = v.find((x) => x.plateNo === plate) ?? null;
      this.known.set(car);
      if (car?.lastMileage) this.form.patchValue({ mileage: car.lastMileage });
    });
  }

  protected save(): void {
    const v = this.form.getRawValue();
    const known = this.known();
    this.busy.set(true);
    this.api.post<Job>('workshop/jobs', {
      customerId: null, customerName: known ? null : v.customerName, phone: known ? null : v.phone, plateNo: v.plateNo,
      make: v.make || null, model: v.model || null, year: v.year ? Number(v.year) : null, color: v.color || null, receivedOn: todayIso(),
      promisedOn: v.promisedOn || null, mileage: v.mileage ? Number(v.mileage) : null, complaint: v.complaint, technicianName: v.technicianName || null, lines: [],
      deposit: Number(this.deposit.value) > 0 ? { amount: Number(this.deposit.value), accountId: this.account.value, method: 'Cash' } : null
    }).subscribe({
      next: (j) => this.ref.close(j),
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }
}

/** One job card: the estimate (parts and labour), status, advances, delivery with the invoice, messages to the customer. */
@Component({
  selector: 'app-workshop-job',
  imports: [...SHARED, DecimalPipe],
  template: `
    <h2 mat-dialog-title><span class="ltr">{{ j().jobNo }}</span> · <b class="ltr">{{ j().plateNo }}</b> · {{ j().customerName }}
      <span class="chip" [class]="tone(j().status)">{{ 'ws.status.' + j().status | t }}</span></h2>
    <mat-dialog-content>
      <div class="facts">
        <div><span class="secondary">{{ 'ws.car' | t }}</span> {{ j().vehicle || '—' }}</div>
        <div><span class="secondary">{{ 'ws.mileage' | t }}</span> <span class="ltr">{{ j().mileage | number }}</span></div>
        <div><span class="secondary">{{ 'ws.received' | t }}</span> <span class="ltr">{{ j().receivedOn | d }}</span></div>
        <div><span class="secondary">{{ 'ws.promised' | t }}</span> <span class="ltr" [class.bad]="j().late">{{ j().promisedOn | d }}</span></div>
        <div><span class="secondary">{{ 'ws.phone' | t }}</span> <span class="ltr">{{ j().phone }}</span></div>
        @if (j().approvedOn) { <div><span class="secondary">{{ 'ws.approved' | t }}</span> <span class="ltr">{{ j().approvedOn | d }}</span></div> }
      </div>
      <p><b>{{ 'ws.complaint' | t }}:</b> {{ j().complaint }}</p>
      @if (open()) {
        <div class="form-grid">
          <mat-form-field class="wide"><mat-label>{{ 'ws.diagnosis' | t }}</mat-label><input matInput [formControl]="diagnosis" /></mat-form-field>
          <mat-form-field><mat-label>{{ 'ws.technician' | t }}</mat-label><input matInput [formControl]="technician" /></mat-form-field>
        </div>
      } @else if (j().diagnosis) { <p><b>{{ 'ws.diagnosis' | t }}:</b> {{ j().diagnosis }}</p> }

      <h3>{{ 'ws.estimate' | t }}</h3>
      <table class="line-editor">
        <thead><tr><th class="kind">{{ 'ws.kind' | t }}</th><th>{{ 'tl.description' | t }}</th><th class="narrow">{{ 'po.qty' | t }}</th>
          <th class="narrow">{{ 'tl.price' | t }}</th><th class="num">{{ 'app.amount' | t }}</th>@if (open()) { <th></th> }</tr></thead>
        <tbody>
          @if (open()) {
            @for (l of lines.controls; track l; let i = $index) {
              <tr [formGroup]="l">
                <td class="kind"><mat-form-field><mat-select formControlName="kind">
                  <mat-option value="Part">{{ 'ws.part' | t }}</mat-option><mat-option value="Labour">{{ 'ws.labour' | t }}</mat-option></mat-select></mat-form-field></td>
                <td><mat-form-field><mat-select formControlName="itemId" (selectionChange)="picked(i)" [placeholder]="'ws.pickItem' | t">
                  <mat-option [value]="null">—</mat-option>
                  @for (c of itemsFor(l.value.kind); track c.id) { <mat-option [value]="c.id">{{ c | bi }} <span class="ltr secondary">{{ c.salesPrice | money }}</span></mat-option> }
                </mat-select></mat-form-field>
                  <mat-form-field><input matInput formControlName="description" [placeholder]="'tl.description' | t" /></mat-form-field></td>
                <td class="narrow"><mat-form-field><input matInput type="number" formControlName="quantity" dir="ltr" /></mat-form-field></td>
                <td class="narrow"><mat-form-field><input matInput type="number" formControlName="unitPrice" dir="ltr" /></mat-form-field></td>
                <td class="num ltr">{{ (l.value.quantity || 0) * (l.value.unitPrice || 0) | money }}</td>
                <td><button mat-icon-button (click)="removeLine(i)"><mat-icon>delete</mat-icon></button></td>
              </tr>
            }
          } @else {
            @for (l of j().lines; track l.lineNo) {
              <tr><td class="kind">{{ (l.kind === 'Part' ? 'ws.part' : 'ws.labour') | t }}</td><td>{{ l.description }}</td><td class="num ltr">{{ l.quantity }}</td>
                <td class="num ltr">{{ l.unitPrice | money }}</td><td class="num ltr">{{ l.amount | money }}</td></tr>
            }
          }
        </tbody>
      </table>
      @if (open()) {
        <div class="row">
          <button mat-button (click)="addLine('Part')"><mat-icon>add</mat-icon> {{ 'ws.addPart' | t }}</button>
          <button mat-button (click)="addLine('Labour')"><mat-icon>add</mat-icon> {{ 'ws.addLabour' | t }}</button>
          <span class="spacer"></span>
          @if (dirty()) { <button mat-stroked-button [disabled]="lines.invalid" (click)="saveLines()"><mat-icon>save</mat-icon> {{ 'ws.saveEstimate' | t }}</button> }
        </div>
      }
      <div class="totals">
        <div>{{ 'app.total' | t }} <b class="ltr">{{ (open() ? draftTotal() : j().total) | money }}</b></div>
        <div>{{ 'tl.paid' | t }} <span class="ltr">{{ j().paid | money }}</span></div>
        <div>{{ 'tl.balance' | t }} <b class="ltr">{{ (open() ? draftTotal() : j().total) - j().paid | money }}</b></div>
      </div>
      @if (open()) {
        <div class="form-grid">
          <mat-form-field><mat-label>{{ 'app.amount' | t }}</mat-label><input matInput type="number" [formControl]="amount" dir="ltr" /></mat-form-field>
          <mat-form-field><mat-label>{{ 'tl.into' | t }}</mat-label>
            <mat-select [formControl]="account">@for (a of accounts(); track a.id) { <mat-option [value]="a.id"><span class="ltr">{{ a.code }}</span> {{ a | bi }}</mat-option> }</mat-select></mat-form-field>
          @if (warehouses().length > 1 && hasParts()) {
            <mat-form-field><mat-label>{{ 'ws.warehouse' | t }}</mat-label>
              <mat-select [formControl]="warehouse">@for (w of warehouses(); track w.id) { <mat-option [value]="w.id">{{ w | bi }}</mat-option> }</mat-select></mat-form-field>
          }
        </div>
      }
    </mat-dialog-content>
    <mat-dialog-actions>
      @if (j().whatsAppPhone) { <a mat-button [href]="whatsApp()" target="_blank" rel="noopener"><mat-icon>chat</mat-icon> WhatsApp</a> }
      @if (aiOn) { <button mat-button (click)="aiMessage()"><mat-icon>auto_awesome</mat-icon> {{ 'ai.message' | t }}</button> }
      <button mat-button (click)="print()"><mat-icon>print</mat-icon> {{ 'ws.print' | t }}</button>
      <span class="spacer"></span>
      @if (open()) {
        <button mat-button (click)="cancel()">{{ 'app.cancel' | t }}</button>
        <button mat-stroked-button [disabled]="!(amount.value! > 0)" (click)="pay()">{{ 'ws.takeAdvance' | t }}</button>
        @for (n of next(); track n) { <button mat-stroked-button (click)="advance(n)">{{ 'ws.to.' + n | t }}</button> }
        @if (j().status === 'Ready') { <button mat-flat-button [disabled]="dirty()" (click)="deliver()">{{ 'ws.deliver' | t }}</button> }
      } @else { <button mat-button mat-dialog-close>{{ 'app.close' | t }}</button> }
    </mat-dialog-actions>
  `,
  styles: `
    .facts { display: grid; grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); gap: 6px 12px; margin-block-end: 8px; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 12px; margin-inline-end: 4px; }
    .bad { color: #8c1d18; } .spacer { flex: 1; } .wide { grid-column: 1 / -1; }
    .kind { width: 110px; }
    .row { display: flex; align-items: center; flex-wrap: wrap; gap: 4px; }
    .totals { display: flex; gap: 24px; justify-content: flex-end; margin-block: 8px; flex-wrap: wrap; }
  `
})
export class JobDialog implements OnInit {
  private readonly api = inject(ApiService);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  private readonly i18n = inject(I18nService);
  private readonly receipt = inject(ReceiptService);
  private readonly dialogs = inject(MatDialog);
  private readonly fb = inject(FormBuilder).nonNullable;
  protected readonly aiOn = inject(AiService).enabled();
  protected readonly j = signal(inject<Job>(MAT_DIALOG_DATA));
  protected readonly accounts = signal<Account[]>([]);
  protected readonly warehouses = signal<Master[]>([]);
  protected readonly catalog = signal<CatalogItem[]>([]);
  protected readonly amount = new FormControl<number | null>(null);
  protected readonly account = new FormControl('', { nonNullable: true });
  protected readonly warehouse = new FormControl('', { nonNullable: true });
  protected readonly diagnosis = new FormControl('', { nonNullable: true });
  protected readonly technician = new FormControl('', { nonNullable: true });
  protected readonly lines = this.fb.array<ReturnType<JobDialog['line']>>([]);
  private readonly version = signal(0);
  protected readonly dirty = signal(false);
  protected readonly open = computed(() => !['Delivered', 'Cancelled'].includes(this.j().status));
  protected readonly next = computed(() => NEXT[this.j().status] ?? []);
  protected readonly hasParts = computed(() => this.j().lines.some((l) => l.kind === 'Part' && l.itemId));
  protected readonly tone = (s: string) => TONE[s] ?? '';
  protected readonly draftTotal = computed(() => {
    this.version();
    return this.lines.getRawValue().reduce((s, l) => s + (Number(l.quantity) || 0) * (Number(l.unitPrice) || 0), 0);
  });

  ngOnInit(): void {
    this.show(this.j());
    moneyAccounts(this.api, (a) => this.accounts.set(a), (id) => this.account.setValue(id));
    this.trade.warehouses().pipe(catchError(() => of([]))).subscribe((w) => {
      this.warehouses.set(w.filter((x) => x.isActive));
      this.warehouse.setValue(w.find((x) => x.code === 'MAIN')?.id ?? w[0]?.id ?? '');
    });
    this.api.get<CatalogItem[]>('workshop/catalog').pipe(catchError(() => of([]))).subscribe((c) => this.catalog.set(c));
    this.lines.valueChanges.subscribe(() => {
      this.version.update((v) => v + 1);
      this.dirty.set(true);
    });
  }

  private line(kind: 'Part' | 'Labour', itemId: string | null = null, description = '', quantity = 1, unitPrice = 0) {
    return this.fb.group({
      kind: [kind], itemId: [itemId], description: [description], quantity: [quantity, Validators.min(0.01)], unitPrice: [unitPrice, Validators.min(0)]
    });
  }

  private show(job: Job): void {
    this.j.set(job);
    this.diagnosis.setValue(job.diagnosis ?? '');
    this.technician.setValue(job.technicianName ?? '');
    this.lines.clear({ emitEvent: false });
    for (const l of job.lines) this.lines.push(this.line(l.kind, l.itemId, l.description, l.quantity, l.unitPrice), { emitEvent: false });
    this.version.update((v) => v + 1);
    this.dirty.set(false);
    this.amount.setValue(job.lines.length && job.balance > 0 ? job.balance : null);
  }

  protected itemsFor(kind: string | undefined): CatalogItem[] {
    return this.catalog().filter((c) => (kind === 'Labour' ? c.type === 'Service' : c.type !== 'Service'));
  }

  protected picked(i: number): void {
    const l = this.lines.at(i);
    const item = this.catalog().find((c) => c.id === l.value.itemId);
    if (item) l.patchValue({ unitPrice: item.salesPrice, description: '' });
  }

  protected addLine(kind: 'Part' | 'Labour'): void {
    this.lines.push(this.line(kind));
  }

  protected removeLine(i: number): void {
    this.lines.removeAt(i);
  }

  private done = (job: Job) => {
    this.show(job);
    this.notify.success();
  };

  protected saveLines(): void {
    const body = this.lines.getRawValue().map((l) => ({
      kind: l.kind, itemId: l.itemId || null, description: l.description || null, quantity: Number(l.quantity), unitPrice: Number(l.unitPrice)
    }));
    this.api.put<Job>(`workshop/jobs/${this.j().id}/lines`, body).subscribe({ next: this.done, error: (e) => this.notify.error(e) });
  }

  protected advance(status: Status): void {
    this.api.post<Job>(`workshop/jobs/${this.j().id}/status`, {
      status, diagnosis: this.diagnosis.value, technicianName: this.technician.value
    }).subscribe({ next: this.done, error: (e) => this.notify.error(e) });
  }

  protected pay(): void {
    this.api.post<Job>(`workshop/jobs/${this.j().id}/payments`, { amount: Number(this.amount.value), accountId: this.account.value, method: 'Cash' })
      .subscribe({ next: this.done, error: (e) => this.notify.error(e) });
  }

  protected deliver(): void {
    const amount = Number(this.amount.value) || 0;
    if (!confirm(this.i18n.t('ws.deliverConfirm'))) return;
    this.api.post<Job>(`workshop/jobs/${this.j().id}/deliver`, {
      date: todayIso(), warehouseId: this.hasParts() ? this.warehouse.value || null : null,
      finalPayment: amount > 0 ? { amount, accountId: this.account.value, method: 'Cash' } : null
    }).subscribe({ next: this.done, error: (e) => this.notify.error(e) });
  }

  protected cancel(): void {
    if (!confirm(this.i18n.t('ws.cancelConfirm'))) return;
    this.api.post<Job>(`workshop/jobs/${this.j().id}/cancel`, {}).subscribe({ next: this.done, error: (e) => this.notify.error(e) });
  }

  protected aiMessage(): void {
    const j = this.j();
    AiDraftDialog.open(this.dialogs, {
      kind: j.status === 'Ready' ? 'order_ready' : 'custom',
      phone: j.whatsAppPhone,
      facts: {
        customer: j.customerName, car: `${j.vehicle ?? ''} ${j.plateNo}`.trim(), job_no: j.jobNo, work: j.lines.map((l) => l.description).join(', '),
        total: j.total.toFixed(2), balance_to_pay: j.balance.toFixed(2), diagnosis: j.diagnosis ?? ''
      }
    });
  }

  /** Ready: "your car is ready"; waiting for approval: the estimate; otherwise "we received your car". */
  protected whatsApp(): string {
    const j = this.j();
    const key = j.status === 'Ready' ? 'ws.msg.ready' : j.status === 'AwaitingApproval' ? 'ws.msg.estimate' : 'ws.msg.received';
    const items = j.lines.map((l) => `- ${l.description}: ${l.amount.toFixed(2)}`).join('\n');
    const text = this.i18n.t(key, { name: j.customerName, plate: j.plateNo, no: j.jobNo, total: j.total.toFixed(2), balance: j.balance.toFixed(2), items });
    return `https://wa.me/${j.whatsAppPhone}?text=${encodeURIComponent(text)}`;
  }

  protected print(): void {
    const j = this.j();
    const t = (k: Parameters<I18nService['t']>[0]) => this.i18n.t(k);
    const esc = (s: string | null | undefined) => (s ?? '').replace(/[&<>]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' })[c]!);
    const date = (d: string | null) => (d ?? '').split('-').reverse().join('/');
    const rows = j.lines.map((l) => `<tr><td>${esc(l.description)}</td><td class="n">${l.quantity}</td><td class="n">${l.amount.toFixed(2)}</td></tr>`).join('');
    this.receipt.printPage(j.jobNo, `
      <style>.grid{display:grid;grid-template-columns:1fr 1fr;gap:4px 16px;margin:8px 0}.job td,.job th{padding:4px;border-bottom:1px solid #ccc}
      .sign{margin-top:40px;display:flex;justify-content:space-between}h2{text-align:start}</style>
      <h2>${t('ws.jobCard')} <span class="ltr">${esc(j.jobNo)}</span></h2>
      <div class="grid"><div>${t('ws.customer')}: ${esc(j.customerName)} <span class="ltr">${esc(j.phone)}</span></div><div>${t('ws.plate')}: <b class="ltr">${esc(j.plateNo)}</b></div>
      <div>${t('ws.car')}: ${esc(j.vehicle)}</div><div>${t('ws.mileage')}: ${j.mileage ?? ''}</div>
      <div>${t('ws.received')}: ${date(j.receivedOn)}</div><div>${t('ws.promised')}: ${date(j.promisedOn)}</div></div>
      <p style="text-align:start"><b>${t('ws.complaint')}:</b> ${esc(j.complaint)}</p>${j.diagnosis ? `<p style="text-align:start"><b>${t('ws.diagnosis')}:</b> ${esc(j.diagnosis)}</p>` : ''}
      <table class="job"><tr><th>${t('tl.description')}</th><th class="n">${t('po.qty')}</th><th class="n">${t('app.amount')}</th></tr>${rows}
      <tr><th colspan="2">${t('app.total')}</th><th class="n">${j.total.toFixed(2)}</th></tr>
      <tr><td colspan="2">${t('tl.paid')}</td><td class="n">${j.paid.toFixed(2)}</td></tr></table>
      <div class="sign"><span>${t('ws.customerSignature')}: ____________</span><span>${t('ws.technician')}: ${esc(j.technicianName)}</span></div>`);
  }
}
