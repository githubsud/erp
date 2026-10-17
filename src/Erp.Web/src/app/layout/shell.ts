import { BreakpointObserver } from '@angular/cdk/layout';
import { Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatListModule } from '@angular/material/list';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { map } from 'rxjs';
import { AuthService } from '../core/auth.service';
import { I18nService } from '../core/i18n/i18n.service';
import { SHARED } from '../shared/shared';

interface NavItem {
  icon: string;
  label: string;
  link: string;
  permission?: string;
}

interface NavGroup {
  label?: string;
  items: NavItem[];
}

const NAV: NavGroup[] = [
  { items: [{ icon: 'space_dashboard', label: 'nav.dashboard', link: '/dashboard' }] },
  {
    label: 'nav.payroll',
    items: [
      { icon: 'badge', label: 'nav.employees', link: '/employees', permission: 'hr.employees:read' },
      { icon: 'payments', label: 'nav.payroll', link: '/payroll', permission: 'hr.payroll:read' },
      { icon: 'more_time', label: 'nav.payrollInputs', link: '/payroll-inputs', permission: 'hr.payroll:read' },
      { icon: 'beach_access', label: 'nav.leave', link: '/leave', permission: 'hr.employees:read' },
      { icon: 'event_busy', label: 'nav.documents', link: '/documents', permission: 'hr.employees:read' },
      { icon: 'handshake', label: 'nav.settlements', link: '/settlements', permission: 'hr.payroll:read' },
      { icon: 'savings', label: 'nav.gratuity', link: '/gratuity', permission: 'hr.payroll:read' }
    ]
  },
  {
    label: 'nav.finance',
    items: [
      { icon: 'account_tree', label: 'nav.accounts', link: '/finance/accounts', permission: 'fin.accounts:read' },
      { icon: 'balance', label: 'nav.trialBalance', link: '/finance/trial-balance', permission: 'fin.reports:read' },
      { icon: 'receipt_long', label: 'nav.journal', link: '/finance/journal', permission: 'fin.journal:read' }
    ]
  },
  {
    label: 'nav.settings',
    items: [
      { icon: 'tune', label: 'nav.hrSettings', link: '/settings/payroll', permission: 'hr.payroll:read' },
      { icon: 'apartment', label: 'nav.company', link: '/settings/company' },
      { icon: 'group', label: 'nav.users', link: '/settings/users', permission: 'core.users:read' }
    ]
  }
];

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatSidenavModule, MatToolbarModule, MatListModule, ...SHARED],
  template: `
    <mat-sidenav-container class="shell">
      <mat-sidenav #drawer [mode]="handset() ? 'over' : 'side'" [opened]="!handset()" [position]="'start'" class="drawer">
        <div class="brand">
          <mat-icon class="brand-icon">diversity_3</mat-icon>
          <div>
            <div class="brand-name">{{ 'app.name' | t }}</div>
            <div class="brand-user">{{ auth.user() | bi:'displayName' }}</div>
          </div>
        </div>
        <mat-nav-list>
          @for (group of nav(); track $index) {
            @if (group.label) {
              <div class="group-label">{{ group.label | t }}</div>
            }
            @for (item of group.items; track item.link) {
              <a mat-list-item [routerLink]="item.link" routerLinkActive="active" (click)="handset() && drawer.close()">
                <mat-icon matListItemIcon>{{ item.icon }}</mat-icon>
                <span matListItemTitle>{{ item.label | t }}</span>
              </a>
            }
          }
        </mat-nav-list>
      </mat-sidenav>

      <mat-sidenav-content>
        <mat-toolbar class="toolbar">
          <button mat-icon-button (click)="drawer.toggle()" [attr.aria-label]="'nav.dashboard' | t">
            <mat-icon>menu</mat-icon>
          </button>
          <span class="spacer"></span>
          <button mat-stroked-button (click)="i18n.toggle()" class="lang">
            <mat-icon>translate</mat-icon>
            {{ 'app.language' | t }}
          </button>
          <button mat-icon-button [matMenuTriggerFor]="userMenu"><mat-icon>account_circle</mat-icon></button>
          <mat-menu #userMenu="matMenu">
            <div class="menu-user">{{ auth.user()?.email }}</div>
            <button mat-menu-item (click)="auth.logout()">
              <mat-icon>logout</mat-icon>
              <span>{{ 'app.logout' | t }}</span>
            </button>
          </mat-menu>
        </mat-toolbar>
        <main class="content">
          <router-outlet />
        </main>
      </mat-sidenav-content>
    </mat-sidenav-container>
  `,
  styles: `
    .shell { height: 100vh; }
    .drawer { width: 272px; border-inline-end: 1px solid var(--mat-sys-outline-variant); }
    .brand { display: flex; gap: 12px; align-items: center; padding: 20px 16px 12px; }
    .brand-icon { color: var(--mat-sys-primary); font-size: 32px; width: 32px; height: 32px; }
    .brand-name { font-weight: 700; color: var(--mat-sys-primary); }
    .brand-user { font-size: 12px; color: var(--mat-sys-on-surface-variant); }
    .group-label { padding: 16px 16px 4px; font-size: 12px; font-weight: 600; color: var(--mat-sys-on-surface-variant); }
    a.active { background: var(--mat-sys-secondary-container); }
    .toolbar { position: sticky; top: 0; z-index: 2; background: var(--mat-sys-surface); border-bottom: 1px solid var(--mat-sys-outline-variant); gap: 8px; }
    .spacer { flex: 1; }
    .lang mat-icon { margin-inline-end: 4px; }
    .menu-user { padding: 8px 16px; font-size: 12px; color: var(--mat-sys-on-surface-variant); }
    .content { padding: 24px; max-width: 1400px; margin-inline: auto; }
    @media (max-width: 600px) { .content { padding: 16px; } }
  `
})
export class Shell {
  protected readonly auth = inject(AuthService);
  protected readonly i18n = inject(I18nService);
  protected readonly handset = toSignal(
    inject(BreakpointObserver).observe('(max-width: 960px)').pipe(map((r) => r.matches)),
    { initialValue: false }
  );
  protected readonly nav = computed(() =>
    NAV.map((g) => ({ ...g, items: g.items.filter((i) => !i.permission || this.auth.can(i.permission)) })).filter((g) => g.items.length > 0)
  );
}
