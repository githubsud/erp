import { Component, inject, signal } from '@angular/core';
import { MAT_DIALOG_DATA, MatDialog } from '@angular/material/dialog';
import { Observable } from 'rxjs';
import { ApiService } from '../core/api.service';
import { NotifyService } from '../core/notify.service';
import { SHARED } from './shared';

export interface ImportSummary {
  dryRun: boolean;
  committed: boolean;
  totalRows: number;
  validRows: number;
  created: number;
  updated: number;
  rows: { row: number; code: string | null; name: string | null; ok: boolean; updated: boolean; errors: string[] }[];
  errors: string[];
}

/** What is imported: its API base (template at `<base>/template`, upload at `<base>`) and the texts that describe it. */
export interface ImportKind {
  base: string;
  title: string;
  hint: string;
  file: string;
}

export const IMPORTS = {
  items: { base: 'inventory/imports/items', title: 'xl.items', hint: 'xl.itemsHint', file: 'items_import_template.xlsx' },
  customers: { base: 'sales/imports/customers', title: 'xl.customers', hint: 'xl.customersHint', file: 'customers_import_template.xlsx' },
  suppliers: { base: 'purchasing/imports/suppliers', title: 'xl.suppliers', hint: 'xl.suppliersHint', file: 'suppliers_import_template.xlsx' }
} satisfies Record<string, ImportKind>;

/**
 * Excel import in three steps: get the template, check the file (nothing is saved and every problem is listed with its row),
 * then import. The server keeps all or nothing, so a half-imported file cannot happen.
 */
@Component({
  selector: 'app-import-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ kind.title | t }}</h2>
    <mat-dialog-content>
      <p class="secondary">{{ kind.hint | t }}</p>
      <ol class="steps">
        <li><button mat-stroked-button (click)="template()"><mat-icon>download</mat-icon> {{ 'xl.template' | t }}</button>
          <span class="secondary small">{{ 'xl.templateHint' | t }}</span></li>
        <li><button mat-stroked-button (click)="picker.click()"><mat-icon>upload_file</mat-icon> {{ 'xl.choose' | t }}</button>
          <input #picker type="file" hidden accept=".xlsx" (change)="pick(picker)" />
          @if (file(); as f) { <span class="chip ltr">{{ f.name }}</span> }</li>
        <li><button mat-flat-button [disabled]="!file() || busy()" (click)="check()"><mat-icon>fact_check</mat-icon> {{ 'xl.check' | t }}</button></li>
      </ol>
      @if (busy()) { <mat-progress-bar mode="indeterminate" /> }

      @if (result(); as r) {
        @if (r.committed) {
          <p class="banner ok"><mat-icon>task_alt</mat-icon> {{ 'xl.done' | t: { created: r.created, updated: r.updated } }}</p>
        } @else if (r.validRows === r.totalRows && !r.errors.length) {
          <p class="banner ok"><mat-icon>check_circle</mat-icon> {{ 'xl.allGood' | t: { n: r.totalRows, created: r.created, updated: r.updated } }}</p>
        } @else {
          <p class="banner bad"><mat-icon>error</mat-icon> {{ 'xl.someBad' | t: { bad: r.totalRows - r.validRows, n: r.totalRows } }}</p>
          @for (e of r.errors; track e) { <p class="error">{{ e }}</p> }
          <div class="table-wrap">
            <table class="line-editor">
              <thead><tr><th>{{ 'xl.row' | t }}</th><th>{{ 'xl.code' | t }}</th><th>{{ 'xl.problem' | t }}</th></tr></thead>
              <tbody>
                @for (row of bad(); track row.row) {
                  <tr><td class="ltr">{{ row.row }}</td><td class="ltr">{{ row.code }}</td><td><div class="errors">@for (e of row.errors; track e) { <div>{{ e }}</div> }</div></td></tr>
                }
              </tbody>
            </table>
          </div>
          <p class="secondary small">{{ 'xl.fixHint' | t }}</p>
        }
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button [mat-dialog-close]="imported()">{{ (imported() ? 'app.close' : 'app.cancel') | t }}</button>
      @if (canImport()) {
        <button mat-flat-button [disabled]="busy()" (click)="run()"><mat-icon>publish</mat-icon> {{ 'xl.import' | t }}</button>
      }
    </mat-dialog-actions>
  `,
  styles: `
    .secondary { color: var(--mat-sys-on-surface-variant); }
    .small { font-size: 12px; }
    .steps { padding-inline-start: 20px; display: flex; flex-direction: column; gap: 10px; }
    .steps li { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
    .banner { display: flex; gap: 8px; align-items: center; padding: 10px 12px; border-radius: 10px; }
    .banner.ok { background: #e3f3ea; color: #1b6e3a; }
    .banner.bad { background: #fde7e6; color: #8c1d18; }
    .error { color: #8c1d18; }
    .errors { font-size: 13px; }
    .table-wrap { max-height: 280px; overflow: auto; }
  `
})
export class ImportDialog {
  protected readonly kind = inject<ImportKind>(MAT_DIALOG_DATA);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly file = signal<File | null>(null);
  protected readonly result = signal<ImportSummary | null>(null);
  protected readonly busy = signal(false);
  protected readonly imported = signal(false);

  protected bad = () => (this.result()?.rows ?? []).filter((r) => !r.ok);
  protected canImport = () => {
    const r = this.result();
    return !!r && !r.committed && r.validRows === r.totalRows && !r.errors.length;
  };

  protected template(): void {
    this.api.download(`${this.kind.base}/template`, undefined, this.kind.file).subscribe({ error: (e) => this.notify.error(e) });
  }

  protected pick(input: HTMLInputElement): void {
    this.file.set(input.files?.[0] ?? null);
    this.result.set(null);
    input.value = '';
  }

  protected check(): void {
    this.send(true).subscribe({ next: (r) => this.done(r), error: (e) => this.failed(e) });
  }

  protected run(): void {
    this.send(false).subscribe({
      next: (r) => {
        this.done(r);
        if (r.committed) this.imported.set(true);
      },
      error: (e) => this.failed(e)
    });
  }

  private send(dryRun: boolean): Observable<ImportSummary> {
    const form = new FormData();
    form.append('file', this.file()!, this.file()!.name);
    this.busy.set(true);
    return this.api.post<ImportSummary>(`${this.kind.base}?dryRun=${dryRun}`, form);
  }

  private done(r: ImportSummary): void {
    this.result.set(r);
    this.busy.set(false);
  }

  private failed(e: unknown): void {
    this.busy.set(false);
    this.notify.error(e);
  }
}

/** Opens the import window; emits true when something was imported, so the page reloads its list. */
export function openImport(dialog: MatDialog, kind: ImportKind): Observable<boolean | undefined> {
  return dialog.open(ImportDialog, { data: kind, width: '680px', maxWidth: '96vw', autoFocus: false }).afterClosed();
}
