import { HttpClient } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { ApiService } from '../../core/api.service';
import { ConfigService } from '../../core/config.service';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

interface ImportRow {
  row: number;
  empNo: string | null;
  name: string | null;
  ok: boolean;
  errors: string[];
}

interface ImportResult {
  dryRun: boolean;
  committed: boolean;
  totalRows: number;
  validRows: number;
  rows: ImportRow[];
}

@Component({
  selector: 'app-employee-import',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <a mat-icon-button routerLink="/employees"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
      <h1>{{ 'import.title' | t }}</h1>
    </div>

    <mat-card appearance="outlined" class="steps">
      <ol>
        <li>
          {{ 'import.step1' | t }}
          <button mat-stroked-button (click)="template()"><mat-icon>download</mat-icon> {{ 'import.template' | t }}</button>
        </li>
        <li>{{ 'import.step2' | t }}</li>
        <li>
          {{ 'import.step3' | t }}
          <input #picker type="file" accept=".xlsx" hidden (change)="pick(picker.files)" />
          <button mat-stroked-button (click)="picker.click()"><mat-icon>upload_file</mat-icon> {{ 'import.choose' | t }}</button>
          @if (file()) { <span class="file ltr">{{ file()!.name }}</span> }
        </li>
      </ol>
      <div class="actions">
        <button mat-stroked-button [disabled]="!file() || busy()" (click)="run(true)"><mat-icon>fact_check</mat-icon> {{ 'import.validate' | t }}</button>
        <button mat-flat-button [disabled]="!canImport() || busy()" (click)="run(false)"><mat-icon>cloud_upload</mat-icon> {{ 'import.import' | t }}</button>
      </div>
      @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
    </mat-card>

    @if (result(); as r) {
      <div class="summary" [class.ok]="r.validRows === r.totalRows" [class.bad]="r.validRows !== r.totalRows">
        @if (r.committed) {
          {{ 'import.done' | t: { n: r.totalRows } }}
        } @else {
          {{ 'import.summary' | t: { valid: r.validRows, total: r.totalRows } }}
        }
      </div>
      <div class="table-wrap">
        <table mat-table [dataSource]="r.rows">
          <ng-container matColumnDef="row">
            <th mat-header-cell *matHeaderCellDef>#</th>
            <td mat-cell *matCellDef="let x">{{ x.row }}</td>
          </ng-container>
          <ng-container matColumnDef="emp">
            <th mat-header-cell *matHeaderCellDef>{{ 'employee.empNo' | t }}</th>
            <td mat-cell *matCellDef="let x"><span class="ltr">{{ x.empNo }}</span> {{ x.name }}</td>
          </ng-container>
          <ng-container matColumnDef="status">
            <th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
            <td mat-cell *matCellDef="let x">
              @if (x.ok) { <span class="chip ok">✓</span> } @else {
                <ul class="errors">@for (e of x.errors; track $index) { <li>{{ e }}</li> }</ul>
              }
            </td>
          </ng-container>
          <tr mat-header-row *matHeaderRowDef="columns"></tr>
          <tr mat-row *matRowDef="let row; columns: columns"></tr>
        </table>
      </div>
    }
  `,
  styles: `
    .steps { padding: 8px 20px 20px; margin-bottom: 16px; }
    ol { line-height: 2.6; padding-inline-start: 20px; }
    li button { margin-inline-start: 12px; }
    .file { margin-inline-start: 8px; color: var(--mat-sys-primary); }
    .summary { padding: 12px 16px; border-radius: 12px; margin-bottom: 12px; font-weight: 600; }
    .summary.ok { background: #d8f3e5; color: #0b5a35; }
    .summary.bad { background: #ffdcd9; color: #8c1d18; }
    .errors { margin: 4px 0; padding-inline-start: 18px; color: #8c1d18; }
  `
})
export class EmployeeImportPage {
  private readonly api = inject(ApiService);
  private readonly http = inject(HttpClient);
  private readonly config = inject(ConfigService);
  private readonly notify = inject(NotifyService);
  private readonly router = inject(Router);

  protected readonly columns = ['row', 'emp', 'status'];
  protected readonly file = signal<File | null>(null);
  protected readonly result = signal<ImportResult | null>(null);
  protected readonly busy = signal(false);
  protected readonly canImport = computed(() => {
    const r = this.result();
    return !!this.file() && !!r && r.dryRun && r.validRows === r.totalRows && r.totalRows > 0;
  });

  protected template(): void {
    this.api.download('hr/imports/employees/template', undefined, 'employees_import_template.xlsx').subscribe({ error: (e) => this.notify.error(e) });
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
    this.http.post<ImportResult>(`${this.config.apiBaseUrl}/api/hr/imports/employees`, form, { params: { dryRun } }).subscribe({
      next: (r) => {
        this.busy.set(false);
        this.result.set(r);
        if (r.committed) {
          this.notify.success();
          setTimeout(() => void this.router.navigate(['/employees']), 1500);
        }
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }
}
