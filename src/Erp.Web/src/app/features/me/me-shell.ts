import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { PwaService } from '../../core/pwa.service';
import { SHARED } from '../../shared/shared';

const TABS = [
  { icon: 'home', label: 'me.nav.home', link: '/me', exact: true },
  { icon: 'receipt_long', label: 'me.nav.payslips', link: '/me/payslips', exact: false },
  { icon: 'beach_access', label: 'me.nav.leave', link: '/me/leave', exact: false },
  { icon: 'badge', label: 'me.nav.documents', link: '/me/documents', exact: false },
  { icon: 'menu', label: 'me.nav.more', link: '/me/more', exact: false }
];

/** Phone-first layout for employees: a slim top bar and bottom navigation, installable as a home-screen app. */
@Component({
  selector: 'app-me-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ...SHARED],
  template: `
    <div class="me">
      <header class="bar">
        <mat-icon class="logo">diversity_3</mat-icon>
        <div class="who">
          <div class="hello">{{ 'me.hello' | t }}</div>
          <div class="name">{{ auth.user() | bi:'displayName' }}</div>
        </div>
        @if (pwa.canInstall() || pwa.iosHint()) {
          <button mat-icon-button (click)="pwa.install()" [attr.aria-label]="'pwa.install' | t"><mat-icon>install_mobile</mat-icon></button>
        }
        <button mat-icon-button (click)="i18n.toggle()" [attr.aria-label]="'app.language' | t"><mat-icon>translate</mat-icon></button>
      </header>
      <main class="body"><router-outlet /></main>
      <nav class="tabs">
        @for (tab of tabs; track tab.link) {
          <a [routerLink]="tab.link" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: tab.exact }">
            <mat-icon>{{ tab.icon }}</mat-icon>
            <span>{{ tab.label | t }}</span>
          </a>
        }
      </nav>
    </div>
  `,
  styles: `
    .me { min-height: 100vh; overflow-x: hidden; display: flex; flex-direction: column; background: var(--mat-sys-surface-container-low); }
    .bar { position: sticky; top: 0; z-index: 2; display: flex; align-items: center; gap: 12px; padding: 10px 16px;
      padding-top: max(10px, env(safe-area-inset-top)); background: var(--mat-sys-primary); color: var(--mat-sys-on-primary); }
    .bar button { color: inherit; }
    .logo { font-size: 28px; width: 28px; height: 28px; }
    .who { flex: 1; min-width: 0; }
    .hello { font-size: 12px; opacity: 0.85; }
    .name { font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .body { flex: 1; box-sizing: border-box; width: 100%; min-width: 0; max-width: 640px; margin-inline: auto; padding: 16px 16px calc(88px + env(safe-area-inset-bottom)); }
    .tabs { position: fixed; inset-inline: 0; bottom: 0; z-index: 2; display: flex; background: var(--mat-sys-surface);
      border-top: 1px solid var(--mat-sys-outline-variant); padding-bottom: env(safe-area-inset-bottom); }
    .tabs a { flex: 1; display: flex; flex-direction: column; align-items: center; gap: 2px; padding: 8px 2px 10px;
      color: var(--mat-sys-on-surface-variant); text-decoration: none; font-size: 11px; }
    .tabs a.active { color: var(--mat-sys-primary); font-weight: 600; }
    .tabs a.active mat-icon { background: var(--mat-sys-secondary-container); border-radius: 16px; width: 56px; }
  `
})
export class MeShell {
  protected readonly auth = inject(AuthService);
  protected readonly i18n = inject(I18nService);
  protected readonly pwa = inject(PwaService);
  protected readonly tabs = TABS;
}
