import { Component, inject } from '@angular/core';
import { FormBuilder } from '@angular/forms';
import { MatDialogRef } from '@angular/material/dialog';
import { MatRadioModule } from '@angular/material/radio';
import { I18nService } from '../core/i18n/i18n.service';
import { PaperWidth, ReceiptService } from '../core/receipt.service';
import { SHARED } from './shared';

/** The receipt printer of this device (kept in this browser only: each till can have a different printer). */
@Component({
  selector: 'app-printer-settings',
  imports: [...SHARED, MatRadioModule],
  template: `
    <h2 mat-dialog-title>{{ 'printer.title' | t }}</h2>
    <mat-dialog-content [formGroup]="form">
      <p class="secondary">{{ 'printer.deviceHint' | t }}</p>
      <label class="label">{{ 'printer.paper' | t }}</label>
      <mat-radio-group formControlName="paper" class="papers">
        <mat-radio-button value="80">{{ 'printer.p80' | t }}</mat-radio-button>
        <mat-radio-button value="58">{{ 'printer.p58' | t }}</mat-radio-button>
        <mat-radio-button value="a4">{{ 'printer.a4' | t }}</mat-radio-button>
      </mat-radio-group>
      <mat-slide-toggle formControlName="autoPrint">{{ 'printer.autoPrint' | t }}</mat-slide-toggle>
      <div class="tips">
        <p><mat-icon>bolt</mat-icon> {{ 'printer.kioskTip' | t }}</p>
        <code class="ltr">chrome.exe --kiosk-printing</code>
        <p><mat-icon>point_of_sale</mat-icon> {{ 'printer.drawerTip' | t }}</p>
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button (click)="test()"><mat-icon>print</mat-icon> {{ 'printer.test' | t }}</button>
      <span class="spacer"></span>
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button (click)="save()">{{ 'app.save' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `
    .secondary { color: var(--mat-sys-on-surface-variant); }
    .label { display: block; font-weight: 500; margin-block-start: 8px; }
    .papers { display: flex; flex-direction: column; margin-block: 4px 12px; }
    .tips { margin-block-start: 16px; padding: 12px; border-radius: 12px; background: var(--mat-sys-surface-container); font-size: 0.9rem; }
    .tips p { display: flex; gap: 8px; align-items: flex-start; margin: 4px 0; }
    .tips mat-icon { flex: none; font-size: 20px; width: 20px; height: 20px; }
    code { display: block; margin: 4px 0 8px; padding: 4px 8px; background: var(--mat-sys-surface); border-radius: 6px; }
    .spacer { flex: 1; }
  `
})
export class PrinterSettingsDialog {
  private readonly receipt = inject(ReceiptService);
  private readonly i18n = inject(I18nService);
  private readonly ref = inject(MatDialogRef<PrinterSettingsDialog>);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    paper: [this.receipt.settings().paper as PaperWidth],
    autoPrint: [this.receipt.settings().autoPrint]
  });

  protected test(): void {
    const paper = this.form.getRawValue().paper;
    const sample = {
      title: this.i18n.t('printer.testTitle'),
      number: '0001',
      meta: [new Date().toLocaleString('en-GB')],
      lines: [
        { text: this.i18n.t('printer.sampleItem'), sub: '2 × 7.50', amount: 15 },
        { text: this.i18n.t('printer.sampleItem2'), sub: '1 × 4.25', amount: 4.25 }
      ],
      totals: [{ label: this.i18n.t('app.total'), amount: 19.25, bold: true }]
    };
    this.receipt.print(sample, paper);
  }

  protected save(): void {
    this.receipt.saveSettings(this.form.getRawValue());
    this.ref.close(true);
  }
}
