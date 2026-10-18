import { Component, inject, signal } from '@angular/core';
import { AbstractControl, FormBuilder, ValidationErrors, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

const MIN_LENGTH = 10;

const matching = (group: AbstractControl): ValidationErrors | null =>
  group.get('newPassword')?.value === group.get('confirm')?.value ? null : { mismatch: true };

/** Choosing a new password; forced after signing in with a temporary password from HR. */
@Component({
  selector: 'app-change-password',
  imports: [...SHARED],
  template: `
    <div class="wrap">
      <mat-card class="card" appearance="outlined">
        <div class="top">
          <mat-icon class="logo">password</mat-icon>
          <button mat-button (click)="i18n.toggle()"><mat-icon>translate</mat-icon> {{ 'app.language' | t }}</button>
        </div>
        <h1>{{ 'password.title' | t }}</h1>
        <p class="sub">{{ (auth.mustChangePassword() ? 'password.forced' : 'password.hint') | t: { min: min } }}</p>
        <form [formGroup]="form" (ngSubmit)="submit()">
          <mat-form-field>
            <mat-label>{{ (auth.mustChangePassword() ? 'password.temporary' : 'password.current') | t }}</mat-label>
            <input matInput type="password" formControlName="currentPassword" autocomplete="current-password" dir="ltr" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'password.new' | t }}</mat-label>
            <input matInput type="password" formControlName="newPassword" autocomplete="new-password" dir="ltr" />
            <mat-hint>{{ 'password.minLength' | t: { min: min } }}</mat-hint>
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'password.confirm' | t }}</mat-label>
            <input matInput type="password" formControlName="confirm" autocomplete="new-password" dir="ltr" />
          </mat-form-field>
          @if (form.hasError('mismatch') && form.controls.confirm.dirty) {
            <div class="error-panel" role="alert">{{ 'password.mismatch' | t }}</div>
          }
          @if (error()) { <div class="error-panel" role="alert">{{ error() }}</div> }
          <button mat-flat-button type="submit" [disabled]="form.invalid || busy()" class="submit">{{ 'password.save' | t }}</button>
          @if (auth.mustChangePassword()) {
            <button mat-button type="button" (click)="auth.logout()">{{ 'app.logout' | t }}</button>
          } @else {
            <button mat-button type="button" (click)="back()">{{ 'app.back' | t }}</button>
          }
        </form>
        @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
      </mat-card>
    </div>
  `,
  styles: `
    .wrap { min-height: 100vh; display: grid; place-items: center; padding: 16px; background: var(--mat-sys-surface-container-low); }
    .card { width: min(420px, 100%); padding: 28px; }
    .top { display: flex; justify-content: space-between; align-items: center; }
    .logo { font-size: 40px; width: 40px; height: 40px; color: var(--mat-sys-primary); }
    h1 { font: var(--mat-sys-headline-small); margin: 12px 0 4px; }
    .sub { color: var(--mat-sys-on-surface-variant); margin: 0 0 20px; }
    form { display: flex; flex-direction: column; gap: 4px; }
    .submit { height: 44px; margin-top: 8px; }
  `
})
export class ChangePasswordPage {
  protected readonly i18n = inject(I18nService);
  protected readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly notify = inject(NotifyService);
  protected readonly min = MIN_LENGTH;
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = inject(FormBuilder).nonNullable.group(
    {
      currentPassword: ['', Validators.required],
      newPassword: ['', [Validators.required, Validators.minLength(MIN_LENGTH)]],
      confirm: ['', Validators.required]
    },
    { validators: matching }
  );

  submit(): void {
    if (this.form.invalid) return;
    const { currentPassword, newPassword } = this.form.getRawValue();
    this.busy.set(true);
    this.error.set(null);
    this.auth.changePassword(currentPassword, newPassword).subscribe({
      next: () => {
        this.notify.success('password.changed');
        void this.router.navigateByUrl(this.auth.home());
      },
      error: (e) => {
        this.busy.set(false);
        this.error.set(this.notify.message(e));
      }
    });
  }

  back(): void {
    void this.router.navigateByUrl(this.auth.home());
  }
}
