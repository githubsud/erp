import { Component, inject, signal } from '@angular/core';
import { FormBuilder, FormControl, FormGroup, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

const LAST_COMPANY = 'erp.lastCompany';

@Component({
  selector: 'app-login',
  imports: [...SHARED],
  template: `
    <div class="wrap">
      <mat-card class="card" appearance="outlined">
        <div class="top">
          <mat-icon class="logo">diversity_3</mat-icon>
          <button mat-button (click)="i18n.toggle()"><mat-icon>translate</mat-icon> {{ 'app.language' | t }}</button>
        </div>
        <h1>{{ 'app.name' | t }}</h1>
        <p class="sub">{{ 'login.subtitle' | t }}</p>

        @if (!mfaToken()) {
          <form [formGroup]="form" (ngSubmit)="submit()">
            <mat-form-field>
              <mat-label>{{ 'login.companyCode' | t }}</mat-label>
              <input matInput formControlName="tenantCode" autocomplete="organization" dir="ltr" />
              <mat-icon matSuffix>apartment</mat-icon>
            </mat-form-field>
            <mat-form-field>
              <mat-label>{{ 'login.email' | t }}</mat-label>
              <input matInput formControlName="email" autocomplete="username" autocapitalize="none" dir="ltr" />
              <mat-icon matSuffix>person</mat-icon>
              <mat-hint>{{ 'login.emailHint' | t }}</mat-hint>
            </mat-form-field>
            <mat-form-field>
              <mat-label>{{ 'login.password' | t }}</mat-label>
              <input matInput type="password" formControlName="password" autocomplete="current-password" dir="ltr" />
              <mat-icon matSuffix>lock</mat-icon>
            </mat-form-field>
            <mat-checkbox formControlName="remember">{{ 'login.remember' | t }}</mat-checkbox>
            <div class="remember-hint">{{ 'login.rememberHint' | t }}</div>
            @if (error()) { <div class="error-panel" role="alert">{{ error() }}</div> }
            <button mat-flat-button type="submit" [disabled]="form.invalid || busy()" class="submit">{{ 'login.submit' | t }}</button>
          </form>
        } @else {
          <form [formGroup]="mfaForm" (ngSubmit)="verify()">
            <p class="mfa-hint"><mat-icon>verified_user</mat-icon> {{ 'login.mfaHint' | t }}</p>
            <mat-form-field>
              <mat-label>{{ 'login.code' | t }}</mat-label>
              <input matInput [formControl]="code" inputmode="numeric" autocomplete="one-time-code" maxlength="6" dir="ltr" class="code" />
            </mat-form-field>
            @if (error()) { <div class="error-panel" role="alert">{{ error() }}</div> }
            <button mat-flat-button type="submit" [disabled]="code.invalid || busy()" class="submit">{{ 'login.verify' | t }}</button>
            <button mat-button type="button" (click)="mfaToken.set(null)">{{ 'app.back' | t }}</button>
          </form>
        }
        @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
      </mat-card>
    </div>
  `,
  styles: `
    .wrap { min-height: 100vh; display: grid; place-items: center; padding: 16px;
      background: radial-gradient(circle at 15% 10%, color-mix(in srgb, var(--mat-sys-primary) 14%, transparent), transparent 55%),
        var(--mat-sys-surface-container-low); }
    .card { width: min(420px, 100%); padding: 28px; }
    .top { display: flex; justify-content: space-between; align-items: center; }
    .logo { font-size: 40px; width: 40px; height: 40px; color: var(--mat-sys-primary); }
    h1 { font: var(--mat-sys-headline-small); margin: 12px 0 4px; }
    .sub { color: var(--mat-sys-on-surface-variant); margin: 0 0 20px; }
    form { display: flex; flex-direction: column; gap: 4px; }
    .remember-hint { font-size: 12px; color: var(--mat-sys-on-surface-variant); margin-inline-start: 40px; margin-top: -8px; }
    .submit { height: 44px; margin-top: 8px; }
    .mfa-hint { display: flex; gap: 8px; align-items: center; color: var(--mat-sys-on-surface-variant); }
    .code { letter-spacing: 0.4em; font-size: 20px; text-align: center; }
  `
})
export class LoginPage {
  protected readonly i18n = inject(I18nService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly notify = inject(NotifyService);

  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly mfaToken = signal<string | null>(null);
  protected readonly code = new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.pattern(/^\d{6}$/)] });
  protected readonly mfaForm = new FormGroup({ code: this.code });
  protected readonly form = inject(FormBuilder).nonNullable.group({
    tenantCode: [localStorage.getItem(LAST_COMPANY) ?? '', Validators.required],
    email: ['', Validators.required],
    password: ['', Validators.required],
    // The installed app on the owner's phone or the till stays signed in unless they untick it.
    remember: [window.matchMedia('(display-mode: standalone)').matches]
  });

  submit(): void {
    if (this.form.invalid) return;
    const { tenantCode, email, password, remember } = this.form.getRawValue();
    this.start();
    this.auth.login(tenantCode.trim(), email.trim(), password, remember).subscribe({
      next: (r) => {
        localStorage.setItem(LAST_COMPANY, tenantCode.trim());
        if (r.mfaRequired && r.mfaToken) {
          this.busy.set(false);
          this.mfaToken.set(r.mfaToken);
        } else {
          void this.router.navigateByUrl(this.auth.home());
        }
      },
      error: (e) => this.fail(e)
    });
  }

  verify(): void {
    const token = this.mfaToken();
    if (!token || this.code.invalid) return;
    this.start();
    this.auth.verifyMfa(token, this.code.value).subscribe({
      next: () => void this.router.navigateByUrl(this.auth.home()),
      error: (e) => {
        this.code.reset();
        this.fail(e);
      }
    });
  }

  private start(): void {
    this.busy.set(true);
    this.error.set(null);
  }

  private fail(e: unknown): void {
    this.busy.set(false);
    this.error.set(this.notify.message(e));
  }
}
