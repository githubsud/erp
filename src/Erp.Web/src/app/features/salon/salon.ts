import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { filter } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { addDaysIso, monthEndIso, monthStartIso, todayIso } from '../../core/dates';
import { I18nService } from '../../core/i18n/i18n.service';
import { Account } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

interface Staff {
  id: string;
  name: string;
  commissionPercent: number;
  color: string | null;
  isActive: boolean;
}

interface Service {
  id: string;
  sku: string;
  nameEn: string;
  nameAr: string;
  price: number;
}

interface Appointment {
  id: string;
  customerName: string;
  phone: string | null;
  whatsAppPhone: string | null;
  staffId: string;
  staffName: string;
  itemId: string | null;
  serviceNameEn: string | null;
  serviceNameAr: string | null;
  startsAt: string;
  minutes: number;
  status: 'Booked' | 'Arrived' | 'Done' | 'Cancelled' | 'NoShow';
  notes: string | null;
  visitId: string | null;
}

const OPEN_HOUR = 9;
const CLOSE_HOUR = 23;
const SLOT = 30;

/** Minutes since midnight Qatar time. */
function qatarMinutes(iso: string): number {
  const d = new Date(new Date(iso).getTime() + 3 * 3600_000);
  return d.getUTCHours() * 60 + d.getUTCMinutes();
}

function qatarTime(day: string, minutes: number): string {
  const h = String(Math.floor(minutes / 60)).padStart(2, '0');
  const m = String(minutes % 60).padStart(2, '0');
  return `${day}T${h}:${m}:00+03:00`;
}

@Component({
  selector: 'app-salon',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'sl.title' | t }}</h1>
      <span class="spacer"></span>
      <button mat-flat-button (click)="checkout(null)"><mat-icon>point_of_sale</mat-icon> {{ 'sl.walkIn' | t }}</button>
    </div>
    <mat-tab-group mat-stretch-tabs="false" (selectedIndexChange)="tab.set($event)">
      <mat-tab [label]="'sl.calendar' | t">
        <div class="toolbar">
          <button mat-icon-button (click)="move(-1)"><mat-icon class="flip-rtl">chevron_left</mat-icon></button>
          <mat-form-field class="date"><input matInput type="date" [formControl]="day" (change)="load()" /></mat-form-field>
          <button mat-icon-button (click)="move(1)"><mat-icon class="flip-rtl">chevron_right</mat-icon></button>
          <button mat-button (click)="day.setValue(today); load()">{{ 'sl.today' | t }}</button>
        </div>
        @if (activeStaff().length) {
          <div class="grid" [style.grid-template-columns]="'64px repeat(' + activeStaff().length + ', minmax(140px, 1fr))'">
            <div class="head"></div>
            @for (s of activeStaff(); track s.id) { <div class="head" [style.border-top-color]="s.color || 'var(--mat-sys-primary)'">{{ s.name }}</div> }
            @for (slot of slots; track slot) {
              <div class="time ltr">{{ label(slot) }}</div>
              @for (s of activeStaff(); track s.id) {
                <div class="cell" (click)="book(s, slot)">
                  @for (a of at(s.id, slot); track a.id) {
                    <div class="appt" [class]="a.status" [style.background]="s.color || null" [style.height.px]="height(a)" (click)="$event.stopPropagation(); open(a)">
                      <b>{{ a.customerName }}</b><div>{{ a | bi:'serviceName' }}</div>
                    </div>
                  }
                </div>
              }
            }
          </div>
        } @else { <p class="secondary">{{ 'sl.noStaff' | t }}</p> }
      </mat-tab>
      <mat-tab [label]="'sl.staff' | t">
        <table class="line-editor tab-body">
          <thead><tr><th>{{ 'sl.name' | t }}</th><th class="num">{{ 'sl.commission' | t }}</th><th>{{ 'app.status' | t }}</th><th></th></tr></thead>
          <tbody>
            @for (s of staff(); track s.id) {
              <tr><td><span class="swatch" [style.background]="s.color"></span> {{ s.name }}</td><td class="num ltr">{{ s.commissionPercent }}%</td>
                <td>{{ (s.isActive ? 'app.yes' : 'app.no') | t }}</td><td><button mat-button (click)="editStaff(s)">{{ 'app.edit' | t }}</button></td></tr>
            }
          </tbody>
        </table>
        <button mat-stroked-button (click)="editStaff(null)"><mat-icon>add</mat-icon> {{ 'sl.addStaff' | t }}</button>
      </mat-tab>
      <mat-tab [label]="'sl.commissionTab' | t">
        <div class="toolbar">
          <mat-form-field class="date"><mat-label>{{ 'app.from' | t }}</mat-label><input matInput type="date" [formControl]="from" /></mat-form-field>
          <mat-form-field class="date"><mat-label>{{ 'app.to' | t }}</mat-label><input matInput type="date" [formControl]="to" /></mat-form-field>
          <button mat-flat-button (click)="loadCommission()">{{ 'roster.run' | t }}</button>
        </div>
        <table class="line-editor">
          <thead><tr><th>{{ 'sl.name' | t }}</th><th class="num">{{ 'sl.services' | t }}</th><th class="num">{{ 'sl.sales' | t }}</th><th class="num">{{ 'sl.commission' | t }}</th></tr></thead>
          <tbody>
            @for (r of commission(); track r.staffId) {
              <tr><td>{{ r.staffName }}</td><td class="num ltr">{{ r.services }}</td><td class="num ltr">{{ r.sales | money }}</td><td class="num ltr"><b>{{ r.commission | money }}</b></td></tr>
            }
          </tbody>
        </table>
        <p class="secondary">{{ 'sl.commissionHint' | t }}</p>
      </mat-tab>
    </mat-tab-group>
  `,
  styles: `
    .spacer { flex: 1; }
    .toolbar { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; margin-block: 8px; }
    .date { width: 170px; }
    .tab-body { margin-block: 16px; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 13px; }
    .grid { display: grid; overflow-x: auto; border: 1px solid var(--mat-sys-outline-variant); border-radius: 12px; }
    .head { position: sticky; top: 0; background: var(--mat-sys-surface-container); padding: 8px; font-weight: 600; text-align: center; border-top: 4px solid transparent; z-index: 1; }
    .time { font-size: 11px; color: var(--mat-sys-on-surface-variant); padding: 2px 6px; border-top: 1px solid var(--mat-sys-outline-variant); }
    .cell { position: relative; height: 32px; border-top: 1px solid var(--mat-sys-outline-variant); border-inline-start: 1px solid var(--mat-sys-outline-variant); cursor: pointer; }
    .cell:hover { background: var(--mat-sys-surface-container-low); }
    .appt { position: absolute; inset-inline: 2px; top: 1px; z-index: 1; border-radius: 6px; padding: 2px 6px; font-size: 12px; overflow: hidden;
      background: var(--mat-sys-primary-container); border: 1px solid var(--mat-sys-outline-variant); }
    .appt.Arrived { outline: 2px solid #2e7d32; }
    .appt.Done { opacity: 0.55; }
    .appt.Cancelled, .appt.NoShow { opacity: 0.35; text-decoration: line-through; }
    .swatch { display: inline-block; width: 12px; height: 12px; border-radius: 3px; vertical-align: middle; }
  `
})
export class SalonPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  protected readonly today = todayIso();
  protected readonly tab = signal(0);
  protected readonly day = new FormControl(todayIso(), { nonNullable: true });
  protected readonly from = new FormControl(monthStartIso(new Date().getFullYear(), new Date().getMonth() + 1), { nonNullable: true });
  protected readonly to = new FormControl(monthEndIso(new Date().getFullYear(), new Date().getMonth() + 1), { nonNullable: true });
  protected readonly staff = signal<Staff[]>([]);
  protected readonly activeStaff = computed(() => this.staff().filter((s) => s.isActive));
  protected readonly services = signal<Service[]>([]);
  protected readonly appointments = signal<Appointment[]>([]);
  protected readonly commission = signal<{ staffId: string; staffName: string; services: number; sales: number; commission: number }[]>([]);
  protected readonly slots = Array.from({ length: ((CLOSE_HOUR - OPEN_HOUR) * 60) / SLOT }, (_, i) => OPEN_HOUR * 60 + i * SLOT);

  ngOnInit(): void {
    this.api.get<Staff[]>('salon/staff').subscribe((s) => this.staff.set(s));
    this.api.get<Service[]>('salon/services').subscribe((s) => this.services.set(s));
    this.load();
    this.loadCommission();
  }

  protected load(): void {
    this.api.get<Appointment[]>('salon/appointments', { day: this.day.value }).subscribe({ next: (a) => this.appointments.set(a), error: (e) => this.notify.error(e) });
  }

  protected loadCommission(): void {
    this.api.get<{ staffId: string; staffName: string; services: number; sales: number; commission: number }[]>('salon/commission', { from: this.from.value, to: this.to.value })
      .subscribe((c) => this.commission.set(c));
  }

  protected move(days: number): void {
    this.day.setValue(addDaysIso(this.day.value, days));
    this.load();
  }

  protected label(m: number): string {
    return `${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`;
  }

  protected at(staffId: string, slot: number): Appointment[] {
    return this.appointments().filter((a) => a.staffId === staffId && qatarMinutes(a.startsAt) >= slot && qatarMinutes(a.startsAt) < slot + SLOT);
  }

  protected height(a: Appointment): number {
    return Math.max(28, (a.minutes / SLOT) * 32 - 4);
  }

  protected book(s: Staff, slot: number): void {
    this.dialog.open(BookDialog, { data: { staff: this.activeStaff(), services: this.services(), staffId: s.id, startsAt: qatarTime(this.day.value, slot) }, width: '480px' })
      .afterClosed().pipe(filter(Boolean)).subscribe(() => this.load());
  }

  protected open(a: Appointment): void {
    this.dialog.open(AppointmentDialog, { data: a, width: '420px' }).afterClosed().subscribe((action?: string) => {
      if (action === 'checkout') this.checkout(a);
      else this.load();
    });
  }

  protected checkout(a: Appointment | null): void {
    this.dialog.open(CheckoutDialog, { data: { appointment: a, staff: this.activeStaff(), services: this.services() }, width: '640px' })
      .afterClosed().pipe(filter(Boolean)).subscribe(() => {
        this.load();
        this.loadCommission();
      });
  }

  protected editStaff(s: Staff | null): void {
    const name = prompt('Name / الاسم', s?.name ?? '');
    if (!name) return;
    const pct = Number(prompt('Commission % / نسبة العمولة', String(s?.commissionPercent ?? 10)));
    const body = { name, commissionPercent: isNaN(pct) ? 0 : pct, color: s?.color ?? null, isActive: true };
    (s ? this.api.put(`salon/staff/${s.id}`, body) : this.api.post('salon/staff', body)).subscribe({
      next: () => this.api.get<Staff[]>('salon/staff').subscribe((x) => this.staff.set(x)),
      error: (e) => this.notify.error(e)
    });
  }
}

@Component({
  selector: 'app-salon-book',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'sl.book' | t }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field><mat-label>{{ 'sl.customer' | t }}</mat-label><input matInput formControlName="customerName" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'tl.phone' | t }}</mat-label><input matInput formControlName="phone" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'sl.staffMember' | t }}</mat-label>
          <mat-select formControlName="staffId">@for (s of data.staff; track s.id) { <mat-option [value]="s.id">{{ s.name }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>{{ 'sl.service' | t }}</mat-label>
          <mat-select formControlName="itemId"><mat-option [value]="null">—</mat-option>
            @for (s of data.services; track s.id) { <mat-option [value]="s.id">{{ s | bi }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>{{ 'sl.time' | t }}</mat-label><input matInput type="time" formControlName="time" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'sl.minutes' | t }}</mat-label><input matInput type="number" formControlName="minutes" dir="ltr" /></mat-form-field>
        <mat-form-field class="wide"><mat-label>{{ 'app.notes' | t }}</mat-label><input matInput formControlName="notes" /></mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid" (click)="save()">{{ 'app.save' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `.wide { grid-column: 1 / -1; }`
})
export class BookDialog {
  protected readonly data = inject<{ staff: Staff[]; services: Service[]; staffId: string; startsAt: string }>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<BookDialog, boolean>);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    customerName: ['', Validators.required], phone: [''], staffId: [this.data.staffId, Validators.required], itemId: [null as string | null],
    time: [this.data.startsAt.slice(11, 16), Validators.required], minutes: [30, [Validators.required, Validators.min(5)]], notes: ['']
  });

  protected save(): void {
    const v = this.form.getRawValue();
    this.api.post('salon/appointments', {
      customerName: v.customerName, phone: v.phone || null, staffId: v.staffId, itemId: v.itemId, startsAt: `${this.data.startsAt.slice(0, 10)}T${v.time}:00+03:00`,
      minutes: Number(v.minutes), notes: v.notes || null
    }).subscribe({ next: () => this.ref.close(true), error: (e) => this.notify.error(e) });
  }
}

@Component({
  selector: 'app-salon-appointment',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ a.customerName }} <span class="chip">{{ 'sl.status.' + a.status | t }}</span></h2>
    <mat-dialog-content>
      <p>{{ a | bi:'serviceName' }} · {{ a.staffName }} · <span class="ltr">{{ time() }}</span> ({{ a.minutes }} {{ 'sl.min' | t }})</p>
      @if (a.notes) { <p>{{ a.notes }}</p> }
      @if (a.phone) { <p class="ltr">{{ a.phone }}</p> }
    </mat-dialog-content>
    <mat-dialog-actions>
      @if (a.whatsAppPhone) { <a mat-button [href]="reminder()" target="_blank" rel="noopener"><mat-icon>chat</mat-icon> WhatsApp</a> }
      <span class="spacer"></span>
      @if (a.status === 'Booked' || a.status === 'Arrived') {
        @if (a.status === 'Booked') { <button mat-button (click)="set('Arrived')">{{ 'sl.arrived' | t }}</button> }
        <button mat-button (click)="set('NoShow')">{{ 'sl.noShow' | t }}</button>
        <button mat-button (click)="set('Cancelled')">{{ 'app.cancel' | t }}</button>
        <button mat-flat-button (click)="ref.close('checkout')">{{ 'sl.checkout' | t }}</button>
      } @else { <button mat-button mat-dialog-close>{{ 'app.close' | t }}</button> }
    </mat-dialog-actions>
  `,
  styles: `.spacer { flex: 1; }`
})
export class AppointmentDialog {
  protected readonly a = inject<Appointment>(MAT_DIALOG_DATA);
  protected readonly ref = inject(MatDialogRef<AppointmentDialog, string>);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  private readonly i18n = inject(I18nService);

  protected time(): string {
    const m = qatarMinutes(this.a.startsAt);
    return `${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`;
  }

  protected reminder(): string {
    const text = this.i18n.t('sl.msg.reminder', { name: this.a.customerName, time: this.time() });
    return `https://wa.me/${this.a.whatsAppPhone}?text=${encodeURIComponent(text)}`;
  }

  protected set(status: string): void {
    this.api.post(`salon/appointments/${this.a.id}/status`, { status }).subscribe({ next: () => this.ref.close('changed'), error: (e) => this.notify.error(e) });
  }
}

@Component({
  selector: 'app-salon-checkout',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'sl.checkout' | t }} @if (data.appointment) { · {{ data.appointment.customerName }} }</h2>
    <mat-dialog-content>
      @if (!data.appointment) {
        <div class="form-grid">
          <mat-form-field><mat-label>{{ 'sl.customer' | t }}</mat-label><input matInput [formControl]="name" /></mat-form-field>
          <mat-form-field><mat-label>{{ 'tl.phone' | t }}</mat-label><input matInput [formControl]="phone" dir="ltr" /></mat-form-field>
        </div>
      }
      <table class="line-editor">
        <thead><tr><th>{{ 'sl.service' | t }}</th><th>{{ 'sl.staffMember' | t }}</th><th class="narrow">{{ 'sl.discount' | t }}</th><th class="num">{{ 'app.amount' | t }}</th><th></th></tr></thead>
        <tbody>
          @for (l of lines.controls; track l; let i = $index) {
            <tr [formGroup]="l">
              <td><mat-form-field><mat-select formControlName="itemId">@for (s of data.services; track s.id) { <mat-option [value]="s.id">{{ s | bi }} · <span class="ltr">{{ s.price | money }}</span></mat-option> }</mat-select></mat-form-field></td>
              <td><mat-form-field><mat-select formControlName="staffId">@for (s of data.staff; track s.id) { <mat-option [value]="s.id">{{ s.name }}</mat-option> }</mat-select></mat-form-field></td>
              <td class="narrow"><mat-form-field><input matInput type="number" formControlName="discountPercent" dir="ltr" /></mat-form-field></td>
              <td class="num ltr">{{ amount(l.value.itemId, l.value.discountPercent) | money }}</td>
              <td><button mat-icon-button (click)="lines.removeAt(i)" [disabled]="lines.length === 1"><mat-icon>delete</mat-icon></button></td>
            </tr>
          }
        </tbody>
      </table>
      <div class="row"><button mat-button (click)="add()"><mat-icon>add</mat-icon> {{ 'sl.addService' | t }}</button><span class="spacer"></span>
        <b>{{ 'app.total' | t }} <span class="ltr">{{ total() | money }}</span></b></div>
      <mat-form-field><mat-label>{{ 'tl.into' | t }}</mat-label>
        <mat-select [formControl]="account">@for (a of accounts(); track a.id) { <mat-option [value]="a.id"><span class="ltr">{{ a.code }}</span> {{ a | bi }}</mat-option> }</mat-select></mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="lines.invalid || !account.value || busy()" (click)="pay()">{{ 'sl.pay' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `.row { display: flex; align-items: center; } .spacer { flex: 1; }`
})
export class CheckoutDialog implements OnInit {
  protected readonly data = inject<{ appointment: Appointment | null; staff: Staff[]; services: Service[] }>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<CheckoutDialog, boolean>);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  private readonly fb = inject(FormBuilder).nonNullable;
  protected readonly accounts = signal<Account[]>([]);
  protected readonly busy = signal(false);
  protected readonly name = new FormControl('', { nonNullable: true });
  protected readonly phone = new FormControl('', { nonNullable: true });
  protected readonly account = new FormControl('', { nonNullable: true });
  protected readonly lines = this.fb.array([this.line(this.data.appointment?.itemId ?? '', this.data.appointment?.staffId ?? this.data.staff[0]?.id ?? '')]);
  private readonly version = signal(0);
  protected readonly total = computed(() => {
    this.version();
    return this.lines.getRawValue().reduce((s, l) => s + this.amount(l.itemId, l.discountPercent), 0);
  });

  ngOnInit(): void {
    this.lines.valueChanges.subscribe(() => this.version.update((v) => v + 1));
    this.api.accounts().subscribe((a) => {
      const list = a.filter((x) => x.isPostable && x.isActive && (x.subtype === 'cash' || x.subtype === 'bank'));
      this.accounts.set(list);
      this.account.setValue(list.find((x) => x.code === '1110')?.id ?? list[0]?.id ?? '');
    });
  }

  private line(itemId = '', staffId = '') {
    return this.fb.group({ itemId: [itemId, Validators.required], staffId: [staffId, Validators.required], discountPercent: [0] });
  }

  protected add(): void {
    this.lines.push(this.line('', this.data.staff[0]?.id ?? ''));
  }

  protected amount(itemId: string | undefined, discount: number | undefined): number {
    const s = this.data.services.find((x) => x.id === itemId);
    return s ? Math.round(s.price * (1 - (Number(discount) || 0) / 100) * 100) / 100 : 0;
  }

  protected pay(): void {
    this.busy.set(true);
    this.api.post('salon/checkout', {
      appointmentId: this.data.appointment?.id ?? null, customerName: this.name.value || null, phone: this.phone.value || null, date: todayIso(),
      accountId: this.account.value, method: 'Cash',
      lines: this.lines.getRawValue().map((l) => ({ itemId: l.itemId, staffId: l.staffId, discountPercent: Number(l.discountPercent) || 0 }))
    }).subscribe({
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
