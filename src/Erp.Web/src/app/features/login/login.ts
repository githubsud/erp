import { Component, inject, signal } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
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

        <form [formGroup]="form" (ngSubmit)="submit()">
          <mat-form-field>
            <mat-label>{{ 'login.companyCode' | t }}</mat-label>
            <input matInput formControlName="tenantCode" autocomplete="organization" dir="ltr" />
            <mat-icon matSuffix>apartment</mat-icon>
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'login.email' | t }}</mat-label>
            <input matInput type="email" formControlName="email" autocomplete="username" dir="ltr" />
            <mat-icon matSuffix>mail</mat-icon>
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'login.password' | t }}</mat-label>
            <input matInput type="password" formControlName="password" autocomplete="current-password" dir="ltr" />
            <mat-icon matSuffix>lock</mat-icon>
          </mat-form-field>

          @if (error()) {
            <div class="error-panel" role="alert">{{ error() }}</div>
          }
          <button mat-flat-button type="submit" [disabled]="form.invalid || busy()" class="submit">
            {{ 'login.submit' | t }}
          </button>
          @if (busy()) {
            <mat-progress-bar mode="indeterminate" />
          }
        </form>
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
    .submit { height: 44px; margin-top: 8px; }
  `
})
export class LoginPage {
  protected readonly i18n = inject(I18nService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly notify = inject(NotifyService);

  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    tenantCode: [localStorage.getItem(LAST_COMPANY) ?? '', Validators.required],
    email: ['', [Validators.required, Validators.email]],
    password: ['', Validators.required]
  });

  submit(): void {
    if (this.form.invalid) return;
    const { tenantCode, email, password } = this.form.getRawValue();
    this.busy.set(true);
    this.error.set(null);
    this.auth.login(tenantCode.trim(), email.trim(), password).subscribe({
      next: () => {
        localStorage.setItem(LAST_COMPANY, tenantCode.trim());
        void this.router.navigate(['/dashboard']);
      },
      error: (e) => {
        this.busy.set(false);
        this.error.set(this.notify.message(e));
      }
    });
  }
}
