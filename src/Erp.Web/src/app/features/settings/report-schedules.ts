import { Component, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

interface ReportInfo {
  key: string;
  nameEn: string;
  nameAr: string;
}

interface Schedule {
  id: string;
  reportKey: string;
  reportNameEn: string;
  reportNameAr: string;
  frequency: 'Daily' | 'Weekly' | 'Monthly';
  weekday: number | null;
  monthDay: number | null;
  recipients: string[];
  ownerName: string;
  isActive: boolean;
  nextRunOn: string;
  lastSentAt: string | null;
  lastError: string | null;
}

const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

/** Reports emailed automatically (daily, weekly or monthly) as CSV files. */
@Component({
  selector: 'app-report-schedules',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'sched.title' | t }}</h1></div>
    <p class="secondary">{{ 'sched.hint' | t }}</p>
    <mat-card appearance="outlined" class="form-card">
      <form [formGroup]="form" class="form-grid">
        <mat-form-field><mat-label>{{ 'sched.report' | t }}</mat-label>
          <mat-select formControlName="reportKey">@for (r of reports(); track r.key) { <mat-option [value]="r.key">{{ r | bi }}</mat-option> }</mat-select>
        </mat-form-field>
        <mat-form-field><mat-label>{{ 'sched.frequency' | t }}</mat-label>
          <mat-select formControlName="frequency">@for (f of ['Daily', 'Weekly', 'Monthly']; track f) { <mat-option [value]="f">{{ 'sched.freq.' + f | t }}</mat-option> }</mat-select>
        </mat-form-field>
        @if (form.value.frequency === 'Weekly') {
          <mat-form-field><mat-label>{{ 'sched.weekday' | t }}</mat-label>
            <mat-select formControlName="weekday">@for (d of days; track d; let i = $index) { <mat-option [value]="i">{{ 'roster.day.' + d | t }}</mat-option> }</mat-select>
          </mat-form-field>
        }
        @if (form.value.frequency === 'Monthly') {
          <mat-form-field><mat-label>{{ 'sched.monthDay' | t }}</mat-label><input matInput type="number" min="1" max="28" formControlName="monthDay" dir="ltr" /></mat-form-field>
        }
        <mat-form-field class="wide"><mat-label>{{ 'sched.recipients' | t }}</mat-label><input matInput formControlName="recipients" dir="ltr" />
          <mat-hint>{{ 'sched.recipientsHint' | t }}</mat-hint></mat-form-field>
        <div><button mat-flat-button type="button" [disabled]="form.invalid" (click)="add()">{{ 'sched.add' | t }}</button></div>
      </form>
    </mat-card>
    <table class="line-editor">
      <thead><tr><th>{{ 'sched.report' | t }}</th><th>{{ 'sched.frequency' | t }}</th><th>{{ 'sched.recipients' | t }}</th><th>{{ 'sched.next' | t }}</th>
        <th>{{ 'sched.last' | t }}</th><th></th></tr></thead>
      <tbody>
        @for (s of schedules(); track s.id) {
          <tr [class.inactive]="!s.isActive">
            <td>{{ s | bi:'reportName' }} <div class="secondary">{{ s.ownerName }}</div></td>
            <td>{{ 'sched.freq.' + s.frequency | t }}
              @if (s.weekday !== null) { · {{ 'roster.day.' + days[s.weekday] | t }} }
              @if (s.monthDay !== null) { · <span class="ltr">{{ s.monthDay }}</span> }</td>
            <td class="ltr small">{{ s.recipients.join(', ') }}</td>
            <td class="ltr">{{ s.nextRunOn | d }}</td>
            <td>@if (s.lastSentAt) { <span class="ltr">{{ s.lastSentAt.slice(0, 10) | d }}</span> }
              @if (s.lastError) { <div class="error">{{ s.lastError }}</div> }</td>
            <td class="actions">
              <button mat-button (click)="sendNow(s)">{{ 'sched.sendNow' | t }}</button>
              <button mat-icon-button (click)="remove(s)"><mat-icon>delete</mat-icon></button>
            </td>
          </tr>
        }
      </tbody>
    </table>
    @if (!schedules().length) { <p class="secondary">{{ 'app.none' | t }}</p> }
  `,
  styles: `
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 13px; }
    .form-card { padding: 16px; margin-block-end: 16px; }
    .wide { min-width: 320px; }
    .small { font-size: 12px; }
    .error { color: var(--mat-sys-error); font-size: 12px; }
    .inactive { opacity: 0.5; }
    .actions { white-space: nowrap; text-align: end; }
  `
})
export class ReportSchedulesPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  private readonly i18n = inject(I18nService);
  protected readonly days = DAYS;
  protected readonly reports = signal<ReportInfo[]>([]);
  protected readonly schedules = signal<Schedule[]>([]);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    reportKey: ['', Validators.required],
    frequency: ['Weekly' as Schedule['frequency']],
    weekday: [0],
    monthDay: [1],
    recipients: ['', Validators.required]
  });

  ngOnInit(): void {
    this.api.get<ReportInfo[]>('core/report-schedules/reports').subscribe((r) => this.reports.set(r));
    this.load();
  }

  private load(): void {
    this.api.get<Schedule[]>('core/report-schedules').subscribe({ next: (s) => this.schedules.set(s), error: (e) => this.notify.error(e) });
  }

  protected add(): void {
    const v = this.form.getRawValue();
    const body = {
      reportKey: v.reportKey, frequency: v.frequency, weekday: v.frequency === 'Weekly' ? v.weekday : null,
      monthDay: v.frequency === 'Monthly' ? Number(v.monthDay) : null, recipients: v.recipients.split(/[,;\s]+/).filter((x) => x)
    };
    this.api.post('core/report-schedules', body).subscribe({
      next: () => {
        this.notify.success();
        this.form.patchValue({ recipients: '' });
        this.load();
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected sendNow(s: Schedule): void {
    this.api.post(`core/report-schedules/${s.id}/send-now`, {}).subscribe({
      next: () => this.notify.success('sched.queued'),
      error: (e) => this.notify.error(e)
    });
  }

  protected remove(s: Schedule): void {
    if (!confirm(this.i18n.t('sched.deleteConfirm'))) return;
    this.api.delete(`core/report-schedules/${s.id}`).subscribe({
      next: () => this.load(),
      error: (e) => this.notify.error(e)
    });
  }
}
