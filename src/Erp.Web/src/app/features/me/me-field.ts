import { Component, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { todayIso } from '../../core/dates';
import { I18nService } from '../../core/i18n/i18n.service';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

interface Punch {
  id: string;
  punchedAt: string;
  kind: 'In' | 'Out';
  siteCode: string | null;
  distanceM: number | null;
  accepted: boolean;
}

interface PunchResult {
  accepted: boolean;
  kind: string;
  siteCode: string | null;
  siteNameEn: string | null;
  siteNameAr: string | null;
  distanceM: number | null;
  workedHours: number | null;
}

interface Claim {
  id: string;
  claimNo: string;
  expenseDate: string;
  category: string;
  description: string;
  amount: number;
  status: 'Submitted' | 'Approved' | 'Rejected' | 'Cancelled';
  decisionNote: string | null;
  payrollYear: number | null;
  payrollMonth: number | null;
}

const STYLES = `
  h2 { font: var(--mat-sys-title-medium); margin: 20px 0 8px; }
  .card { background: var(--mat-sys-surface); border-radius: 16px; padding: 16px; margin-bottom: 12px; border: 1px solid var(--mat-sys-outline-variant); }
  .muted { color: var(--mat-sys-on-surface-variant); font-size: 13px; }
  .list-item { display: flex; align-items: center; gap: 12px; padding: 12px 0; border-bottom: 1px solid var(--mat-sys-outline-variant); }
  .list-item:last-child { border-bottom: 0; }
  .grow { flex: 1; min-width: 0; }
  .empty { text-align: center; color: var(--mat-sys-on-surface-variant); padding: 24px 0; }
  .punch { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
  .punch button { height: 72px; font-size: 1.1rem; }
  .full { width: 100%; }
`;

/** Punch in and out from the phone; the server checks the location against the work sites. */
@Component({
  selector: 'app-me-punch',
  imports: [...SHARED],
  template: `
    <h2>{{ 'me.nav.punch' | t }}</h2>
    <div class="card">
      <p class="muted">{{ 'punch.hint' | t }}</p>
      <div class="punch">
        <button mat-flat-button [disabled]="busy()" (click)="punch('In')"><mat-icon>login</mat-icon> {{ 'punch.in' | t }}</button>
        <button mat-stroked-button [disabled]="busy()" (click)="punch('Out')"><mat-icon class="flip-rtl">logout</mat-icon> {{ 'punch.out' | t }}</button>
      </div>
      @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
      @if (last(); as r) {
        <p [class.muted]="r.accepted">
          @if (r.accepted) { ✓ {{ 'punch.accepted' | t }} · {{ i18n.isArabic() ? r.siteNameAr : r.siteNameEn }}
            @if (r.workedHours !== null) { · <span class="ltr">{{ r.workedHours }}</span> {{ 'punch.hours' | t }} } }
          @else { <b>{{ 'punch.refused' | t: { m: r.distanceM ?? 0 } }}</b> }
        </p>
      }
    </div>
    <h2>{{ 'punch.recent' | t }}</h2>
    <div class="card">
      @for (p of punches(); track p.id) {
        <div class="list-item">
          <mat-icon>{{ p.kind === 'In' ? 'login' : 'logout' }}</mat-icon>
          <div class="grow"><span class="ltr">{{ p.punchedAt.slice(0, 10) | d }} {{ time(p.punchedAt) }}</span>
            <div class="muted ltr">{{ p.siteCode }} · {{ p.distanceM }} m</div></div>
          <span class="chip" [class.ok]="p.accepted" [class.bad]="!p.accepted">{{ (p.accepted ? 'punch.ok' : 'punch.outside') | t }}</span>
        </div>
      } @empty { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `,
  styles: STYLES
})
export class MePunchPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly i18n = inject(I18nService);
  protected readonly busy = signal(false);
  protected readonly last = signal<PunchResult | null>(null);
  protected readonly punches = signal<Punch[]>([]);

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    this.api.get<Punch[]>('me/punches').subscribe((p) => this.punches.set(p));
  }

  protected time(iso: string): string {
    return new Date(iso).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
  }

  protected punch(kind: 'In' | 'Out'): void {
    if (!navigator.geolocation) {
      this.notify.info(this.i18n.t('punch.noGps'));
      return;
    }
    this.busy.set(true);
    navigator.geolocation.getCurrentPosition(
      (pos) => {
        this.api.post<PunchResult>('me/punch', {
          kind, latitude: Number(pos.coords.latitude.toFixed(6)), longitude: Number(pos.coords.longitude.toFixed(6)), accuracyM: Math.round(pos.coords.accuracy)
        }).subscribe({
          next: (r) => {
            this.busy.set(false);
            this.last.set(r);
            this.load();
          },
          error: (e) => {
            this.busy.set(false);
            this.notify.error(e);
          }
        });
      },
      () => {
        this.busy.set(false);
        this.notify.info(this.i18n.t('punch.noGps'));
      },
      { enableHighAccuracy: true, timeout: 15000, maximumAge: 0 }
    );
  }
}

/** Expense claims with a receipt photo taken on the phone. */
@Component({
  selector: 'app-me-expenses',
  imports: [...SHARED],
  template: `
    <h2>{{ 'me.nav.expenses' | t }}</h2>
    <div class="card">
      <form [formGroup]="form">
        <mat-form-field class="full"><mat-label>{{ 'exp.category' | t }}</mat-label>
          <mat-select formControlName="category">@for (c of categories; track c) { <mat-option [value]="c">{{ 'exp.cat.' + c | t }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field class="full"><mat-label>{{ 'app.date' | t }}</mat-label><input matInput type="date" formControlName="expenseDate" /></mat-form-field>
        <mat-form-field class="full"><mat-label>{{ 'app.amount' | t }}</mat-label><input matInput type="number" min="0" formControlName="amount" dir="ltr" /></mat-form-field>
        <mat-form-field class="full"><mat-label>{{ 'exp.description' | t }}</mat-label><input matInput formControlName="description" /></mat-form-field>
        <label class="muted">{{ 'exp.receipt' | t }}</label>
        <input type="file" accept="image/*,application/pdf" capture="environment" (change)="pick($event)" />
        <div style="margin-top: 12px"><button mat-flat-button type="button" class="full" [disabled]="form.invalid || busy()" (click)="submit()">{{ 'exp.submit' | t }}</button></div>
      </form>
    </div>
    <div class="card">
      @for (c of claims(); track c.id) {
        <div class="list-item">
          <div class="grow">
            <div>{{ 'exp.cat.' + c.category | t }} · <span class="ltr">{{ c.amount | money }}</span></div>
            <div class="muted"><span class="ltr">{{ c.expenseDate | d }}</span> · {{ c.description }}</div>
            @if (c.status === 'Approved' && c.payrollMonth) { <div class="muted">{{ 'exp.inPayroll' | t }} <span class="ltr">{{ c.payrollYear }}-{{ c.payrollMonth }}</span></div> }
            @if (c.decisionNote) { <div class="muted">“{{ c.decisionNote }}”</div> }
          </div>
          <span class="chip" [class.ok]="c.status === 'Approved'" [class.warn]="c.status === 'Submitted'" [class.bad]="c.status === 'Rejected'">{{ 'exp.status.' + c.status | t }}</span>
          @if (c.status === 'Submitted') { <button mat-icon-button (click)="cancel(c)"><mat-icon>close</mat-icon></button> }
        </div>
      } @empty { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `,
  styles: STYLES
})
export class MeExpensesPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly categories = ['Fuel', 'Meals', 'Transport', 'Accommodation', 'Supplies', 'Phone', 'Other'];
  protected readonly claims = signal<Claim[]>([]);
  protected readonly busy = signal(false);
  private file: File | null = null;
  protected readonly form = inject(FormBuilder).nonNullable.group({
    category: ['Fuel', Validators.required],
    expenseDate: [todayIso(), Validators.required],
    amount: [null as unknown as number, [Validators.required, Validators.min(0.01)]],
    description: ['', Validators.required]
  });

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    this.api.get<Claim[]>('me/expenses').subscribe((c) => this.claims.set(c));
  }

  protected pick(e: Event): void {
    this.file = (e.target as HTMLInputElement).files?.[0] ?? null;
  }

  protected submit(): void {
    const v = this.form.getRawValue();
    const body = new FormData();
    body.append('category', v.category);
    body.append('expenseDate', v.expenseDate);
    body.append('amount', String(v.amount));
    body.append('description', v.description);
    if (this.file) body.append('receipt', this.file, this.file.name);
    this.busy.set(true);
    this.api.post('me/expenses', body).subscribe({
      next: () => {
        this.busy.set(false);
        this.notify.success();
        this.form.patchValue({ amount: null as unknown as number, description: '' });
        this.file = null;
        this.load();
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }

  protected cancel(c: Claim): void {
    this.api.post(`me/expenses/${c.id}/cancel`, {}).subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }
}
