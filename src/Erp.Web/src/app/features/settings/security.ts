import { Component, inject, signal } from '@angular/core';
import { FormControl, Validators } from '@angular/forms';
import QRCode from 'qrcode';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

interface MfaSetup {
  secret: string;
  otpAuthUri: string;
}

@Component({
  selector: 'app-security',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'security.title' | t }}</h1></div>
    <mat-card appearance="outlined" class="block">
      <div class="row">
        <mat-icon class="big" [class.on]="enabled()">{{ enabled() ? 'verified_user' : 'shield' }}</mat-icon>
        <div>
          <div class="section-title">{{ 'security.mfa' | t }}</div>
          <span class="chip" [class.ok]="enabled()" [class.warn]="!enabled()">{{ (enabled() ? 'security.on' : 'security.off') | t }}</span>
        </div>
      </div>
      <p class="hint">{{ 'security.explain' | t }}</p>

      @if (!enabled() && !setup()) {
        <button mat-flat-button (click)="start()"><mat-icon>qr_code_2</mat-icon> {{ 'security.setup' | t }}</button>
      }

      @if (setup(); as s) {
        <ol class="steps">
          <li>{{ 'security.step1' | t }}</li>
          <li>
            {{ 'security.step2' | t }}
            <div class="qr">
              @if (qr()) { <img [src]="qr()" alt="QR" width="200" height="200" /> }
              <div class="secret ltr">{{ s.secret }}</div>
            </div>
          </li>
          <li>{{ 'security.step3' | t }}</li>
        </ol>
      }

      @if (setup() || enabled()) {
        <div class="confirm">
          <mat-form-field>
            <mat-label>{{ 'login.code' | t }}</mat-label>
            <input matInput [formControl]="code" inputmode="numeric" maxlength="6" dir="ltr" class="code" />
          </mat-form-field>
          @if (enabled()) {
            <button mat-stroked-button (click)="disable()" [disabled]="code.invalid">{{ 'security.disable' | t }}</button>
          } @else {
            <button mat-flat-button (click)="enable()" [disabled]="code.invalid">{{ 'security.enable' | t }}</button>
          }
        </div>
      }
    </mat-card>
  `,
  styles: `
    .block { padding: 20px; max-width: 720px; }
    .row { display: flex; gap: 16px; align-items: center; }
    .big { font-size: 40px; width: 40px; height: 40px; color: var(--mat-sys-on-surface-variant); }
    .big.on { color: #0b5a35; }
    .hint { color: var(--mat-sys-on-surface-variant); }
    .steps { line-height: 1.9; padding-inline-start: 20px; }
    .qr { display: flex; flex-direction: column; align-items: flex-start; gap: 8px; margin: 12px 0; }
    .qr img { border: 1px solid var(--mat-sys-outline-variant); border-radius: 8px; background: white; }
    .secret { font-family: monospace; letter-spacing: 0.1em; }
    .confirm { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; }
    .code { letter-spacing: 0.4em; }
  `
})
export class SecurityPage {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly notify = inject(NotifyService);

  protected readonly enabled = signal(false);
  protected readonly setup = signal<MfaSetup | null>(null);
  protected readonly qr = signal<string | null>(null);
  protected readonly code = new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.pattern(/^\d{6}$/)] });

  constructor() {
    this.api.get<{ enabled: boolean }>('auth/mfa').subscribe((s) => this.enabled.set(s.enabled));
  }

  protected start(): void {
    this.api.post<MfaSetup>('auth/mfa/setup').subscribe({
      next: async (s) => {
        this.setup.set(s);
        this.qr.set(await QRCode.toDataURL(s.otpAuthUri, { margin: 1, width: 200 }));
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected enable(): void {
    this.api.post<{ enabled: boolean }>('auth/mfa/enable', { code: this.code.value }).subscribe({
      next: () => this.done(true),
      error: (e) => this.notify.error(e)
    });
  }

  protected disable(): void {
    this.api.post<{ enabled: boolean }>('auth/mfa/disable', { code: this.code.value }).subscribe({
      next: () => this.done(false),
      error: (e) => this.notify.error(e)
    });
  }

  private done(enabled: boolean): void {
    this.enabled.set(enabled);
    this.setup.set(null);
    this.qr.set(null);
    this.code.reset();
    this.auth.setMfaEnabled(enabled);
    this.notify.success();
  }
}
