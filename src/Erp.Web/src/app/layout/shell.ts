import { BreakpointObserver } from '@angular/cdk/layout';
import { Component, DestroyRef, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatBadgeModule } from '@angular/material/badge';
import { MatDialog } from '@angular/material/dialog';
import { MatListModule } from '@angular/material/list';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { map } from 'rxjs';
import { AuthService } from '../core/auth.service';
import { ModulesService } from '../core/modules.service';
import { I18nService } from '../core/i18n/i18n.service';
import { PwaService } from '../core/pwa.service';
import { SupportService } from '../core/support.service';
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
  {
    items: [
      { icon: 'space_dashboard', label: 'nav.dashboard', link: '/dashboard' },
      { icon: 'approval', label: 'nav.approvals', link: '/approvals' },
      { icon: 'auto_awesome', label: 'nav.ai', link: '/ai', permission: 'ai.assistant:use' }
    ]
  },
  {
    label: 'nav.payroll',
    items: [
      { icon: 'badge', label: 'nav.employees', link: '/employees', permission: 'hr.employees:read' },
      { icon: 'payments', label: 'nav.payroll', link: '/payroll', permission: 'hr.payroll:read' },
      { icon: 'more_time', label: 'nav.payrollInputs', link: '/payroll-inputs', permission: 'hr.payroll:read' },
      { icon: 'directions_walk', label: 'nav.fieldStaff', link: '/field-staff', permission: 'hr.employees:read' },
      { icon: 'calendar_view_week', label: 'nav.rosters', link: '/rosters', permission: 'hr.employees:read' },
      { icon: 'fingerprint', label: 'nav.attendance', link: '/attendance', permission: 'hr.payroll:read' },
      { icon: 'beach_access', label: 'nav.leave', link: '/leave', permission: 'hr.employees:read' },
      { icon: 'event_busy', label: 'nav.documents', link: '/documents', permission: 'hr.employees:read' },
      { icon: 'handshake', label: 'nav.settlements', link: '/settlements', permission: 'hr.payroll:read' },
      { icon: 'savings', label: 'nav.gratuity', link: '/gratuity', permission: 'hr.payroll:read' },
      { icon: 'summarize', label: 'nav.hrReports', link: '/hr-reports', permission: 'hr.payroll:read' }
    ]
  },
  {
    label: 'nav.sales',
    items: [
      { icon: 'content_cut', label: 'nav.salon', link: '/salon', permission: 'sal.salon:use' },
      { icon: 'checkroom', label: 'nav.tailoring', link: '/tailoring', permission: 'sal.tailoring:use' },
      { icon: 'car_repair', label: 'nav.workshop', link: '/workshop', permission: 'sal.workshop:use' },
      { icon: 'point_of_sale', label: 'nav.pos', link: '/sales/pos', permission: 'sal.pos:sell' },
      { icon: 'insights', label: 'nav.crm', link: '/sales/crm', permission: 'sal.crm:use' },
      { icon: 'engineering', label: 'nav.projects', link: '/sales/projects', permission: 'sal.customers:read' },
      { icon: 'storefront', label: 'nav.customers', link: '/sales/customers', permission: 'sal.customers:read' },
      { icon: 'request_page', label: 'nav.quotations', link: '/sales/quotations', permission: 'sal.customers:read' },
      { icon: 'shopping_bag', label: 'nav.salesOrders', link: '/sales/orders', permission: 'sal.customers:read' },
      { icon: 'local_shipping', label: 'nav.deliveries', link: '/sales/deliveries', permission: 'sal.customers:read' },
      { icon: 'receipt', label: 'nav.salesInvoices', link: '/sales/invoices', permission: 'sal.customers:read' },
      { icon: 'payments', label: 'nav.receipts', link: '/sales/receipts', permission: 'sal.customers:read' },
      { icon: 'assignment_return', label: 'nav.creditNotes', link: '/sales/credit-notes', permission: 'sal.customers:read' },
      { icon: 'account_balance_wallet', label: 'nav.customerCredits', link: '/sales/credits', permission: 'sal.customers:read' },
      { icon: 'insights', label: 'nav.salesReports', link: '/sales/reports', permission: 'sal.reports:read' },
      { icon: 'policy', label: 'nav.leaks', link: '/sales/leaks', permission: 'sal.reports:read' }
    ]
  },
  {
    label: 'nav.trade',
    items: [
      { icon: 'factory', label: 'nav.suppliers', link: '/purchasing/suppliers', permission: 'pur.suppliers:read' },
      { icon: 'production_quantity_limits', label: 'nav.reorder', link: '/purchasing/reorder', permission: 'pur.suppliers:read' },
      { icon: 'compare', label: 'nav.sourcing', link: '/purchasing/sourcing', permission: 'pur.suppliers:read' },
      { icon: 'playlist_add', label: 'nav.purchaseRequests', link: '/purchasing/requests', permission: 'pur.requests:manage' },
      { icon: 'shopping_cart', label: 'nav.orders', link: '/purchasing/orders', permission: 'pur.suppliers:read' },
      { icon: 'request_quote', label: 'nav.invoices', link: '/purchasing/invoices', permission: 'pur.suppliers:read' },
      { icon: 'construction', label: 'nav.subcontracts', link: '/purchasing/subcontracts', permission: 'pur.suppliers:read' },
      { icon: 'price_check', label: 'nav.supplierPayments', link: '/purchasing/payments', permission: 'pur.suppliers:read' },
      { icon: 'sailing', label: 'nav.landedCosts', link: '/purchasing/landed-costs', permission: 'pur.suppliers:read' },
      { icon: 'account_balance_wallet', label: 'nav.supplierCredits', link: '/purchasing/credits', permission: 'pur.suppliers:read' },
      { icon: 'category', label: 'nav.items', link: '/inventory/items', permission: 'inv.items:read' },
      { icon: 'warehouse', label: 'nav.stock', link: '/inventory/stock', permission: 'inv.items:read' },
      { icon: 'event_busy', label: 'nav.expiry', link: '/inventory/expiry', permission: 'inv.items:read' },
      { icon: 'calendar_month', label: 'nav.season', link: '/inventory/season', permission: 'inv.reports:read' },
      { icon: 'qr_code_scanner', label: 'nav.serials', link: '/inventory/serials', permission: 'inv.items:read' },
      { icon: 'tune', label: 'nav.adjustments', link: '/inventory/adjustments', permission: 'inv.items:read' },
      { icon: 'swap_horiz', label: 'nav.transfers', link: '/inventory/transfers', permission: 'inv.items:read' },
      { icon: 'fact_check', label: 'nav.counts', link: '/inventory/counts', permission: 'inv.items:read' },
      { icon: 'query_stats', label: 'nav.purchasingReports', link: '/purchasing/reports', permission: 'pur.reports:read' }
    ]
  },
  {
    label: 'nav.finance',
    items: [
      { icon: 'monitoring', label: 'nav.statements', link: '/finance/statements', permission: 'fin.reports:read' },
      { icon: 'edit_note', label: 'nav.vouchers', link: '/finance/vouchers', permission: 'fin.journal:read' },
      { icon: 'account_balance', label: 'nav.bank', link: '/finance/bank', permission: 'fin.bank:reconcile' },
      { icon: 'precision_manufacturing', label: 'nav.fixedAssets', link: '/finance/assets', permission: 'fin.assets:read' },
      { icon: 'query_stats', label: 'nav.budgets', link: '/finance/budgets', permission: 'fin.reports:read' },
      { icon: 'account_tree', label: 'nav.accounts', link: '/finance/accounts', permission: 'fin.accounts:read' },
      { icon: 'balance', label: 'nav.trialBalance', link: '/finance/trial-balance', permission: 'fin.reports:read' },
      { icon: 'receipt_long', label: 'nav.journal', link: '/finance/journal', permission: 'fin.journal:read' },
      { icon: 'start', label: 'nav.openingBalances', link: '/finance/opening-balances', permission: 'fin.journal:post' },
      { icon: 'event_available', label: 'nav.fiscalYears', link: '/finance/years', permission: 'fin.accounts:read' }
    ]
  },
  {
    label: 'nav.settings',
    items: [
      { icon: 'tune', label: 'nav.hrSettings', link: '/settings/payroll', permission: 'hr.payroll:read' },
      { icon: 'inventory_2', label: 'nav.tradeSettings', link: '/settings/trade', permission: 'inv.items:read' },
      { icon: 'rule', label: 'nav.approvalRules', link: '/settings/approvals', permission: 'core.approvals:manage' },
      { icon: 'auto_awesome', label: 'nav.aiSettings', link: '/settings/ai', permission: 'ai.settings:manage' },
      { icon: 'rocket_launch', label: 'nav.setup', link: '/settings/setup', permission: 'core.company:manage' },
      { icon: 'apartment', label: 'nav.company', link: '/settings/company' },
      { icon: 'shield_lock', label: 'nav.security', link: '/settings/security' },
      { icon: 'group', label: 'nav.users', link: '/settings/users', permission: 'core.users:read' },
      { icon: 'manage_search', label: 'nav.audit', link: '/settings/audit', permission: 'core.audit:read' },
      { icon: 'extension', label: 'nav.modules', link: '/settings/modules' },
      { icon: 'schedule_send', label: 'nav.reportSchedules', link: '/settings/report-schedules' }
    ]
  }
];

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatSidenavModule, MatToolbarModule, MatListModule, MatBadgeModule, ...SHARED],
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
          @if (!pwa.online()) {
            <span class="offline" [matTooltip]="'pwa.offlineHint' | t"><mat-icon>cloud_off</mat-icon> {{ 'pwa.offline' | t }}</span>
          }
          <button mat-stroked-button (click)="i18n.toggle()" class="lang">
            <mat-icon>translate</mat-icon>
            {{ 'app.language' | t }}
          </button>
          <button mat-icon-button [matMenuTriggerFor]="helpMenu" [matTooltip]="'help.title' | t" [attr.aria-label]="'help.title' | t">
            <mat-icon [matBadge]="support.unread() || null" matBadgeSize="small" matBadgeColor="warn">support_agent</mat-icon>
          </button>
          <mat-menu #helpMenu="matMenu">
            <button mat-menu-item (click)="newRequest()"><mat-icon>add_comment</mat-icon><span>{{ 'help.new' | t }}</span></button>
            <a mat-menu-item routerLink="/support"><mat-icon>forum</mat-icon><span>{{ 'help.mine' | t }}</span>
              @if (support.unread()) { <span class="chip ok">{{ support.unread() }}</span> }</a>
          </mat-menu>
          <button mat-icon-button [matMenuTriggerFor]="userMenu"><mat-icon>account_circle</mat-icon></button>
          <mat-menu #userMenu="matMenu">
            <div class="menu-user">{{ auth.user()?.email ?? auth.user()?.username }}</div>
            @if (pwa.canInstall() || pwa.iosHint()) {
              <button mat-menu-item (click)="pwa.install()">
                <mat-icon>install_mobile</mat-icon>
                <span>{{ 'pwa.install' | t }}</span>
              </button>
            }
            <a mat-menu-item routerLink="/change-password">
              <mat-icon>password</mat-icon>
              <span>{{ 'password.change' | t }}</span>
            </a>
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
    .offline { display: inline-flex; align-items: center; gap: 4px; padding: 4px 10px; border-radius: 16px; font-size: 13px;
      background: var(--mat-sys-error-container); color: var(--mat-sys-on-error-container); }
    .offline mat-icon { font-size: 18px; width: 18px; height: 18px; }
    @media (max-width: 600px) { .lang { min-width: 0; padding-inline: 8px; } }
    .menu-user { padding: 8px 16px; font-size: 12px; color: var(--mat-sys-on-surface-variant); }
    .content { padding: 24px; max-width: 1400px; margin-inline: auto; }
    @media (max-width: 600px) { .content { padding: 16px; } }
  `
})
export class Shell {
  protected readonly auth = inject(AuthService);
  protected readonly i18n = inject(I18nService);
  protected readonly pwa = inject(PwaService);
  protected readonly handset = toSignal(
    inject(BreakpointObserver).observe('(max-width: 960px)').pipe(map((r) => r.matches)),
    { initialValue: false }
  );
  private readonly modules = inject(ModulesService);
  protected readonly nav = computed(() => {
    const off = this.modules.disabled();
    return NAV.map((g) => ({
      ...g,
      items: g.items.filter((i) => (!i.permission || this.auth.can(i.permission)) && !off.has(ModulesService.moduleOf(i.link) ?? ''))
    })).filter((g) => g.items.length > 0);
  });

  protected readonly support = inject(SupportService);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);

  constructor() {
    this.modules.load();
    this.support.watch(inject(DestroyRef));
  }

  /** The form loads on first use, keeping it out of the start-up bundle. */
  protected newRequest(): void {
    void import('../features/support/support').then((m) => m.openNewRequest(this.dialog, this.router));
  }
}
