import { Component, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { addDaysIso, todayIso } from '../../core/dates';
import { I18nService } from '../../core/i18n/i18n.service';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

interface Claim {
  id: string;
  claimNo: string;
  empNo: string;
  nameEn: string;
  nameAr: string;
  expenseDate: string;
  category: string;
  description: string;
  amount: number;
  status: string;
  hasReceipt: boolean;
  decisionNote: string | null;
  payrollYear: number | null;
  payrollMonth: number | null;
}

interface Site {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  latitude: number;
  longitude: number;
  radiusM: number;
  isActive: boolean;
}

interface Punch {
  id: string;
  empNo: string;
  nameEn: string;
  nameAr: string;
  punchedAt: string;
  kind: string;
  latitude: number;
  longitude: number;
  accuracyM: number | null;
  siteCode: string | null;
  distanceM: number | null;
  accepted: boolean;
}

/** Field staff: expense claims to approve, work sites for phone attendance, and the punch log. */
@Component({
  selector: 'app-field-staff',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'field.title' | t }}</h1></div>
    <mat-tab-group mat-stretch-tabs="false">
      <mat-tab [label]="'field.expenses' | t">
        <div class="toolbar">
          <mat-form-field><mat-label>{{ 'app.status' | t }}</mat-label>
            <mat-select [formControl]="status" (selectionChange)="loadClaims()">
              <mat-option value="">{{ 'field.all' | t }}</mat-option>
              @for (s of ['Submitted', 'Approved', 'Rejected', 'Cancelled']; track s) { <mat-option [value]="s">{{ 'exp.status.' + s | t }}</mat-option> }
            </mat-select></mat-form-field>
        </div>
        <table class="line-editor">
          <thead><tr><th>{{ 'field.claim' | t }}</th><th>{{ 'roster.employee' | t }}</th><th>{{ 'app.date' | t }}</th><th>{{ 'exp.category' | t }}</th>
            <th class="num">{{ 'app.amount' | t }}</th><th>{{ 'app.status' | t }}</th><th></th></tr></thead>
          <tbody>
            @for (c of claims(); track c.id) {
              <tr>
                <td class="ltr">{{ c.claimNo }}</td>
                <td><span class="ltr secondary">{{ c.empNo }}</span> {{ c | bi }}</td>
                <td class="ltr">{{ c.expenseDate | d }}</td>
                <td>{{ 'exp.cat.' + c.category | t }}<div class="secondary">{{ c.description }}</div></td>
                <td class="num ltr">{{ c.amount | money }}</td>
                <td><span class="chip" [class.ok]="c.status === 'Approved'" [class.warn]="c.status === 'Submitted'" [class.bad]="c.status === 'Rejected'">{{ 'exp.status.' + c.status | t }}</span>
                  @if (c.payrollMonth) { <div class="secondary ltr">{{ c.payrollYear }}-{{ c.payrollMonth }}</div> }</td>
                <td class="actions">
                  @if (c.hasReceipt) { <button mat-icon-button (click)="receipt(c)" [matTooltip]="'exp.receipt' | t"><mat-icon>receipt</mat-icon></button> }
                  @if (c.status === 'Submitted' && canApprove) {
                    <button mat-button (click)="decide(c, false)">{{ 'approval.reject' | t }}</button>
                    <button mat-flat-button (click)="decide(c, true)">{{ 'approval.approve' | t }}</button>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
        @if (!claims().length) { <p class="secondary">{{ 'app.none' | t }}</p> }
        <p class="secondary">{{ 'field.expenseHint' | t }}</p>
      </mat-tab>
      <mat-tab [label]="'field.sites' | t">
        <table class="line-editor tab-body">
          <thead><tr><th>{{ 'roster.code' | t }}</th><th>{{ 'roster.name' | t }}</th><th>{{ 'field.location' | t }}</th><th class="num">{{ 'field.radius' | t }}</th></tr></thead>
          <tbody>
            @for (s of sites(); track s.id) {
              <tr [class.inactive]="!s.isActive"><td class="ltr">{{ s.code }}</td><td>{{ s | bi }}</td>
                <td class="ltr"><a [href]="'https://www.openstreetmap.org/?mlat=' + s.latitude + '&mlon=' + s.longitude + '#map=17/' + s.latitude + '/' + s.longitude" target="_blank" rel="noopener">{{ s.latitude }}, {{ s.longitude }}</a></td>
                <td class="num ltr">{{ s.radiusM }} m</td></tr>
            }
          </tbody>
        </table>
        @if (canManage) {
          <form [formGroup]="siteForm" class="form-grid">
            <mat-form-field><mat-label>{{ 'roster.code' | t }}</mat-label><input matInput formControlName="code" dir="ltr" /></mat-form-field>
            <mat-form-field><mat-label>{{ 'roster.nameEn' | t }}</mat-label><input matInput formControlName="nameEn" /></mat-form-field>
            <mat-form-field><mat-label>{{ 'roster.nameAr' | t }}</mat-label><input matInput formControlName="nameAr" /></mat-form-field>
            <mat-form-field><mat-label>{{ 'field.latitude' | t }}</mat-label><input matInput type="number" formControlName="latitude" dir="ltr" /></mat-form-field>
            <mat-form-field><mat-label>{{ 'field.longitude' | t }}</mat-label><input matInput type="number" formControlName="longitude" dir="ltr" /></mat-form-field>
            <mat-form-field><mat-label>{{ 'field.radius' | t }}</mat-label><input matInput type="number" formControlName="radiusM" dir="ltr" /></mat-form-field>
            <div class="actions-row">
              <button mat-button type="button" (click)="here()"><mat-icon>my_location</mat-icon> {{ 'field.useHere' | t }}</button>
              <button mat-flat-button type="button" [disabled]="siteForm.invalid" (click)="addSite()">{{ 'app.save' | t }}</button>
            </div>
          </form>
        }
      </mat-tab>
      <mat-tab [label]="'field.punches' | t">
        <div class="toolbar">
          <mat-form-field><mat-label>{{ 'app.from' | t }}</mat-label><input matInput type="date" [formControl]="from" /></mat-form-field>
          <mat-form-field><mat-label>{{ 'app.to' | t }}</mat-label><input matInput type="date" [formControl]="to" /></mat-form-field>
          <button mat-flat-button (click)="loadPunches()">{{ 'roster.run' | t }}</button>
        </div>
        <table class="line-editor">
          <thead><tr><th>{{ 'app.date' | t }}</th><th>{{ 'roster.employee' | t }}</th><th>{{ 'field.kind' | t }}</th><th>{{ 'field.site' | t }}</th>
            <th class="num">{{ 'field.distance' | t }}</th><th>{{ 'app.status' | t }}</th></tr></thead>
          <tbody>
            @for (p of punches(); track p.id) {
              <tr>
                <td class="ltr">{{ p.punchedAt.slice(0, 10) | d }} {{ time(p.punchedAt) }}</td>
                <td><span class="ltr secondary">{{ p.empNo }}</span> {{ p | bi }}</td>
                <td>{{ (p.kind === 'In' ? 'punch.in' : 'punch.out') | t }}</td>
                <td class="ltr">{{ p.siteCode }}</td>
                <td class="num ltr">{{ p.distanceM }} m @if (p.accuracyM) { <span class="secondary">±{{ p.accuracyM }}</span> }</td>
                <td><span class="chip" [class.ok]="p.accepted" [class.bad]="!p.accepted">{{ (p.accepted ? 'punch.ok' : 'punch.outside') | t }}</span></td>
              </tr>
            }
          </tbody>
        </table>
        @if (!punches().length) { <p class="secondary">{{ 'app.none' | t }}</p> }
      </mat-tab>
    </mat-tab-group>
  `,
  styles: `
    .toolbar { display: flex; flex-wrap: wrap; gap: 8px; align-items: baseline; margin-block: 12px; }
    .tab-body { margin-block-start: 16px; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 12px; }
    .actions { white-space: nowrap; text-align: end; }
    .inactive { opacity: 0.5; }
    .actions-row { display: flex; gap: 8px; align-items: center; }
  `
})
export class FieldStaffPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  private readonly i18n = inject(I18nService);
  private readonly auth = inject(AuthService);
  protected readonly canApprove = this.auth.can('hr.expenses:approve');
  protected readonly canManage = this.auth.can('hr.settings:manage');
  protected readonly claims = signal<Claim[]>([]);
  protected readonly sites = signal<Site[]>([]);
  protected readonly punches = signal<Punch[]>([]);
  protected readonly status = new FormControl('Submitted', { nonNullable: true });
  protected readonly from = new FormControl(addDaysIso(todayIso(), -6), { nonNullable: true });
  protected readonly to = new FormControl(todayIso(), { nonNullable: true });
  protected readonly siteForm = inject(FormBuilder).nonNullable.group({
    code: ['', Validators.required], nameEn: ['', Validators.required], nameAr: ['', Validators.required],
    latitude: [25.2854, Validators.required], longitude: [51.531, Validators.required], radiusM: [200, [Validators.required, Validators.min(20), Validators.max(5000)]]
  });

  ngOnInit(): void {
    this.loadClaims();
    this.loadSites();
    this.loadPunches();
  }

  protected loadClaims(): void {
    this.api.get<Claim[]>('hr/expenses', { status: this.status.value || undefined }).subscribe({ next: (c) => this.claims.set(c), error: (e) => this.notify.error(e) });
  }

  private loadSites(): void {
    this.api.get<Site[]>('hr/work-sites').subscribe((s) => this.sites.set(s));
  }

  protected loadPunches(): void {
    this.api.get<Punch[]>('hr/punches', { from: this.from.value, to: this.to.value }).subscribe({ next: (p) => this.punches.set(p), error: (e) => this.notify.error(e) });
  }

  protected time(iso: string): string {
    return new Date(iso).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
  }

  protected receipt(c: Claim): void {
    this.api.download(`hr/expenses/${c.id}/receipt`, undefined, `${c.claimNo}`).subscribe({ error: (e) => this.notify.error(e) });
  }

  protected decide(c: Claim, approve: boolean): void {
    const note = approve ? null : prompt(this.i18n.t('field.rejectReason'));
    if (!approve && !note) return;
    this.api.post(`hr/expenses/${c.id}/decide`, { approve, note }).subscribe({
      next: () => {
        this.notify.success();
        this.loadClaims();
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected here(): void {
    navigator.geolocation?.getCurrentPosition((pos) =>
      this.siteForm.patchValue({ latitude: Number(pos.coords.latitude.toFixed(6)), longitude: Number(pos.coords.longitude.toFixed(6)) }));
  }

  protected addSite(): void {
    const v = this.siteForm.getRawValue();
    this.api.post('hr/work-sites', { ...v, latitude: Number(v.latitude), longitude: Number(v.longitude), radiusM: Number(v.radiusM), isActive: true }).subscribe({
      next: () => {
        this.notify.success();
        this.siteForm.patchValue({ code: '', nameEn: '', nameAr: '' });
        this.loadSites();
      },
      error: (e) => this.notify.error(e)
    });
  }
}
