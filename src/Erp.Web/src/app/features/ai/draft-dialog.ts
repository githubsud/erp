import { Component, inject, signal } from '@angular/core';
import { FormControl } from '@angular/forms';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MAT_DIALOG_DATA, MatDialog } from '@angular/material/dialog';
import { AiService, DraftKind } from '../../core/ai.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

export interface DraftDialogData {
  kind: DraftKind;
  facts: Record<string, string>;
  phone?: string | null;
}

/** The AI writes a WhatsApp message from the screen's facts; the user edits it and sends it from WhatsApp. Nothing is sent automatically. */
@Component({
  selector: 'app-ai-draft-dialog',
  imports: [...SHARED, MatButtonToggleModule],
  template: `
    <h2 mat-dialog-title><mat-icon class="spark">auto_awesome</mat-icon> {{ 'ai.draft.title' | t }} · {{ 'ai.kind.' + data.kind | t }}</h2>
    <mat-dialog-content>
      <div class="row">
        <mat-button-toggle-group [formControl]="language">
          <mat-button-toggle value="ar">العربية</mat-button-toggle>
          <mat-button-toggle value="en">English</mat-button-toggle>
        </mat-button-toggle-group>
        <mat-form-field class="grow"><mat-label>{{ 'ai.draft.instructions' | t }}</mat-label><input matInput [formControl]="instructions" /></mat-form-field>
        <button mat-flat-button [disabled]="busy()" (click)="write()"><mat-icon>auto_awesome</mat-icon> {{ (text.value ? 'ai.draft.again' : 'ai.draft.write') | t }}</button>
      </div>
      @if (busy()) { <mat-progress-bar mode="indeterminate" /><p class="secondary">{{ 'ai.thinking' | t }}</p> }
      @if (text.value || written()) {
        <mat-form-field class="full"><mat-label>{{ 'ai.draft.message' | t }}</mat-label>
          <textarea matInput rows="7" [formControl]="text" [dir]="language.value === 'ar' ? 'rtl' : 'ltr'"></textarea>
          <mat-hint>{{ 'ai.draft.check' | t }}</mat-hint></mat-form-field>
      }
    </mat-dialog-content>
    <mat-dialog-actions>
      <button mat-button mat-dialog-close>{{ 'app.close' | t }}</button>
      <button mat-button [disabled]="!text.value" (click)="copy()"><mat-icon>content_copy</mat-icon> {{ 'ai.draft.copy' | t }}</button>
      <a mat-flat-button [disabled]="!text.value" [href]="link()" target="_blank" rel="noopener"><mat-icon>chat</mat-icon> WhatsApp</a>
    </mat-dialog-actions>
  `,
  styles: `
    .row { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; margin-block: 8px; }
    .grow { flex: 1; min-width: 200px; }
    .full { width: 100%; margin-top: 8px; }
    .spark { color: var(--mat-sys-primary); vertical-align: middle; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 13px; }
  `
})
export class AiDraftDialog {
  protected readonly data = inject<DraftDialogData>(MAT_DIALOG_DATA);
  private readonly ai = inject(AiService);
  private readonly notify = inject(NotifyService);
  private readonly i18n = inject(I18nService);
  protected readonly language = new FormControl(this.i18n.lang(), { nonNullable: true });
  protected readonly instructions = new FormControl('', { nonNullable: true });
  protected readonly text = new FormControl('', { nonNullable: true });
  protected readonly busy = signal(false);
  protected readonly written = signal(false);
  private url: string | null = null;

  static open(dialog: MatDialog, data: DraftDialogData): void {
    dialog.open(AiDraftDialog, { data, width: '640px', maxWidth: '96vw' });
  }

  constructor() {
    this.write();
  }

  protected write(): void {
    this.busy.set(true);
    this.ai.draft(this.data.kind, this.language.value, this.data.facts, this.data.phone, this.instructions.value || null).subscribe({
      next: (d) => {
        this.text.setValue(d.text);
        this.url = d.whatsAppUrl;
        this.written.set(true);
        this.busy.set(false);
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }

  protected link(): string {
    return AiService.whatsApp(this.url, this.text.value);
  }

  protected copy(): void {
    navigator.clipboard?.writeText(this.text.value).then(() => this.notify.success('ai.draft.copied'));
  }
}
