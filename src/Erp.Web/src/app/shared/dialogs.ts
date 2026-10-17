import { Component, inject, signal } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { map } from 'rxjs';
import { ApiService } from '../core/api.service';
import { todayIso } from '../core/dates';
import { Account } from '../core/models';
import { SHARED } from './shared';

export interface PaymentResult {
  paymentAccountId: string;
  paymentDate: string;
}

/** Choose the bank/cash account and date a payment was made from. */
@Component({
  selector: 'app-payment-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ data.title | t }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field class="full">
          <mat-label>{{ 'payroll.paymentAccount' | t }}</mat-label>
          <mat-select formControlName="paymentAccountId">
            @for (a of accounts(); track a.id) {
              <mat-option [value]="a.id"><span class="ltr">{{ a.code }}</span> · {{ a | bi }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field class="full">
          <mat-label>{{ 'payroll.paymentDate' | t }}</mat-label>
          <input matInput type="date" formControlName="paymentDate" />
        </mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid" (click)="ref.close(form.getRawValue())">{{ 'app.confirm' | t }}</button>
    </mat-dialog-actions>
  `
})
export class PaymentDialog {
  protected readonly data = inject<{ title: string; date?: string }>(MAT_DIALOG_DATA);
  protected readonly ref = inject(MatDialogRef<PaymentDialog, PaymentResult>);
  protected readonly accounts = signal<Account[]>([]);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    paymentAccountId: ['', Validators.required],
    paymentDate: [this.data.date ?? todayIso(), Validators.required]
  });

  constructor() {
    inject(ApiService)
      .accounts()
      .pipe(map((list) => list.filter((a) => a.isPostable && (a.subtype === 'bank' || a.subtype === 'cash'))))
      .subscribe((list) => {
        this.accounts.set(list);
        const bank = list.find((a) => a.subtype === 'bank');
        if (bank) this.form.controls.paymentAccountId.setValue(bank.id);
      });
  }
}

export interface ReverseResult {
  date: string;
  reason: string;
}

/** Reversal date and mandatory reason (kept in the audit trail and the reversing journal entry). */
@Component({
  selector: 'app-reverse-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'payroll.reverse' | t }}</h2>
    <mat-dialog-content>
      @if (data.warning) {
        <p class="warning">{{ data.warning | t }}</p>
      }
      <form [formGroup]="form" class="form-grid">
        <mat-form-field class="full">
          <mat-label>{{ 'app.date' | t }}</mat-label>
          <input matInput type="date" formControlName="date" />
        </mat-form-field>
        <mat-form-field class="full">
          <mat-label>{{ 'app.reason' | t }}</mat-label>
          <textarea matInput formControlName="reason" rows="3"></textarea>
        </mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button color="warn" [disabled]="form.invalid" (click)="ref.close(form.getRawValue())">{{ 'payroll.reverse' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `.warning { color: #8c1d18; }`
})
export class ReverseDialog {
  protected readonly data = inject<{ date?: string; warning?: string }>(MAT_DIALOG_DATA);
  protected readonly ref = inject(MatDialogRef<ReverseDialog, ReverseResult>);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    date: [this.data.date ?? todayIso(), Validators.required],
    reason: ['', [Validators.required, Validators.minLength(3)]]
  });
}

/** Simple yes/no confirmation. */
@Component({
  selector: 'app-confirm-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ data.title | t }}</h2>
    <mat-dialog-content>{{ data.message | t }}</mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button [mat-dialog-close]="false">{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [mat-dialog-close]="true">{{ 'app.confirm' | t }}</button>
    </mat-dialog-actions>
  `
})
export class ConfirmDialog {
  protected readonly data = inject<{ title: string; message: string }>(MAT_DIALOG_DATA);
}
