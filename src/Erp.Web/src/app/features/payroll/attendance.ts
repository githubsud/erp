import { HttpClient } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, Validators } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { ConfigService } from '../../core/config.service';
import { AttendanceImportResult, AttendanceSummary } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';
import { currentMonth } from './payroll-list';

@Component({
  selector: 'app-attendance',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'attendance.title' | t }}</h1>
      <mat-form-field class="month">
        <mat-label>{{ 'payroll.month' | t }}</mat-label>
        <input matInput type="month" [formControl]="month" />
      </mat-form-field>
    </div>

    @if (auth.can('hr.payroll:prepare')) {
      <mat-card appearance="outlined" class="steps">
        <p class="hint">{{ 'attendance.hint' | t }}</p>
        <div class="actions">
          <button mat-stroked-button (click)="template()"><mat-icon>download</mat-icon> {{ 'import.template' | t }}</button>
          <input #picker type="file" accept=".xlsx,.csv" hidden (change)="pick(picker.files); picker.value = ''" />
          <button mat-stroked-button (click)="picker.click()"><mat-icon>upload_file</mat-icon> {{ 'import.choose' | t }}</button>
          @if (file()) { <span class="file ltr">{{ file()!.name }}</span> }
          <span class="spacer"></span>
          <button mat-stroked-button [disabled]="!file() || busy()" (click)="run(true)"><mat-icon>fact_check</mat-icon> {{ 'import.validate' | t }}</button>
          <button mat-flat-button [disabled]="!canImport() || busy()" (click)="run(false)"><mat-icon>cloud_upload</mat-icon> {{ 'import.import' | t }}</button>
        </div>
        @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
      </mat-card>
    }

    @if (result(); as r) {
      <div class="summary" [class.ok]="r.validRows === r.totalRows" [class.bad]="r.validRows !== r.totalRows">
        @if (r.committed) {
          {{ 'attendance.done' | t: { n: r.totalRows } }}
        } @else {
          {{ 'import.summary' | t: { valid: r.validRows, total: r.totalRows } }}
        }
      </div>
      @if (errorRows().length) {
        <div class="table-wrap errors-table">
          <table mat-table [dataSource]="errorRows()">
            <ng-container matColumnDef="row">
              <th mat-header-cell *matHeaderCellDef>#</th>
              <td mat-cell *matCellDef="let x">{{ x.row }}</td>
            </ng-container>
            <ng-container matColumnDef="emp">
              <th mat-header-cell *matHeaderCellDef>{{ 'employee.empNo' | t }}</th>
              <td mat-cell *matCellDef="let x"><span class="ltr">{{ x.empNo }}</span> {{ x.name }}</td>
            </ng-container>
            <ng-container matColumnDef="errors">
              <th mat-header-cell *matHeaderCellDef>{{ 'payroll.errors' | t }}</th>
              <td mat-cell *matCellDef="let x"><ul class="errors">@for (e of x.errors; track $index) { <li>{{ e }}</li> }</ul></td>
            </ng-container>
            <tr mat-header-row *matHeaderRowDef="['row', 'emp', 'errors']"></tr>
            <tr mat-row *matRowDef="let row; columns: ['row', 'emp', 'errors']"></tr>
          </table>
        </div>
      }
    }

    <div class="section-title">{{ (preview() ? 'attendance.preview' : 'attendance.month') | t }}</div>
    <div class="table-wrap">
      <table mat-table [dataSource]="rows()">
        <ng-container matColumnDef="employee">
          <th mat-header-cell *matHeaderCellDef>{{ 'employee.name' | t }}</th>
          <td mat-cell *matCellDef="let r"><span class="ltr">{{ r.empNo }}</span> · {{ r | bi }}</td>
        </ng-container>
        <ng-container matColumnDef="days">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'attendance.days' | t }}</th>
          <td mat-cell *matCellDef="let r" class="num">{{ r.days }}</td>
        </ng-container>
        <ng-container matColumnDef="worked">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'attendance.worked' | t }}</th>
          <td mat-cell *matCellDef="let r" class="num ltr">{{ r.worked }}</td>
        </ng-container>
        @for (c of otColumns; track c.key) {
          <ng-container [matColumnDef]="c.key">
            <th mat-header-cell *matHeaderCellDef class="num">{{ c.label | t }}</th>
            <td mat-cell *matCellDef="let r" class="num ltr">{{ r[c.key] || '' }}</td>
          </ng-container>
        }
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
      </table>
      @if (rows().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `,
  styles: `
    .month { width: 200px; }
    .steps { padding: 4px 20px 16px; margin-bottom: 16px; }
    .hint { color: var(--mat-sys-on-surface-variant); line-height: 1.7; }
    .actions { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
    .spacer { flex: 1; }
    .file { color: var(--mat-sys-primary); }
    .summary { padding: 12px 16px; border-radius: 12px; margin-bottom: 12px; font-weight: 600; }
    .summary.ok { background: #d8f3e5; color: #0b5a35; }
    .summary.bad { background: #ffdcd9; color: #8c1d18; }
    .errors-table { margin-bottom: 16px; }
    .errors { margin: 4px 0; padding-inline-start: 18px; color: #8c1d18; }
  `
})
export class AttendancePage {
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly http = inject(HttpClient);
  private readonly config = inject(ConfigService);
  private readonly notify = inject(NotifyService);

  protected readonly otColumns = [
    { key: 'normal', label: 'ot.Normal' },
    { key: 'night', label: 'ot.Night' },
    { key: 'restDay', label: 'ot.RestDay' },
    { key: 'holiday', label: 'ot.Holiday' }
  ] as const;
  protected readonly columns = ['employee', 'days', 'worked', ...this.otColumns.map((c) => c.key)];
  protected readonly month = new FormControl(currentMonth(), { nonNullable: true, validators: Validators.required });
  protected readonly file = signal<File | null>(null);
  protected readonly result = signal<AttendanceImportResult | null>(null);
  protected readonly saved = signal<AttendanceSummary[]>([]);
  protected readonly busy = signal(false);
  protected readonly preview = computed(() => {
    const r = this.result();
    return !!r && !r.committed && r.employees.length > 0;
  });
  protected readonly rows = computed(() => (this.preview() ? this.result()!.employees : this.saved()));
  protected readonly errorRows = computed(() => this.result()?.rows.filter((r) => !r.ok) ?? []);
  protected readonly canImport = computed(() => {
    const r = this.result();
    return !!this.file() && !!r && r.dryRun && r.validRows === r.totalRows && r.totalRows > 0;
  });

  constructor() {
    this.month.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => this.load());
    this.load();
  }

  protected template(): void {
    this.api.download('hr/imports/attendance/template', undefined, 'attendance_import_template.xlsx').subscribe({ error: (e) => this.notify.error(e) });
  }

  protected pick(files: FileList | null): void {
    this.file.set(files?.item(0) ?? null);
    this.result.set(null);
  }

  protected run(dryRun: boolean): void {
    const file = this.file();
    if (!file) return;
    const form = new FormData();
    form.append('file', file);
    this.busy.set(true);
    this.http.post<AttendanceImportResult>(`${this.config.apiBaseUrl}/api/hr/imports/attendance`, form, { params: { dryRun } }).subscribe({
      next: (r) => {
        this.busy.set(false);
        this.result.set(r);
        if (r.committed) {
          this.notify.success();
          this.file.set(null);
          this.load();
        }
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }

  private load(): void {
    const [year, month] = this.month.value.split('-').map(Number);
    if (!year || !month) return;
    this.api.attendance(year, month).subscribe({ next: (r) => this.saved.set(r), error: (e) => this.notify.error(e) });
  }
}
