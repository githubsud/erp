import { BreakpointObserver } from '@angular/cdk/layout';
import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { MatListModule } from '@angular/material/list';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { Router, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter, map } from 'rxjs';
import {
  BackOfficeService, BoInvoice, BoModule, BoPlan, BoSummary, BUSINESS_TYPES, CustomerDetail, CustomerRow
} from '../../core/backoffice.service';
import { todayIso } from '../../core/dates';
import { I18nService } from '../../core/i18n/i18n.service';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

const STATUS_TONE: Record<string, string> = {
  Active: 'ok', Trial: 'info', PastDue: 'warn', Suspended: 'bad', Cancelled: '', Closed: '', Open: 'warn', Paid: 'ok', Void: ''
};

@Component({
  selector: 'app-bo-login',
  imports: [...SHARED],
  template: `
    <div class="wrap">
      <mat-card appearance="outlined" class="card">
        <h1>{{ 'bo.title' | t }}</h1>
        <p class="secondary">{{ 'bo.loginHint' | t }}</p>
        <form [formGroup]="form" (ngSubmit)="submit()">
          <mat-form-field class="full"><mat-label>{{ 'bo.email' | t }}</mat-label><input matInput formControlName="email" dir="ltr" autocomplete="username" /></mat-form-field>
          <mat-form-field class="full"><mat-label>{{ 'bo.password' | t }}</mat-label>
            <input matInput type="password" formControlName="password" dir="ltr" autocomplete="current-password" /></mat-form-field>
          <button mat-flat-button class="full" [disabled]="form.invalid || busy()">{{ 'bo.signIn' | t }}</button>
        </form>
        <button mat-button (click)="i18n.toggle()">{{ 'app.language' | t }}</button>
      </mat-card>
    </div>
  `,
  styles: `
    .wrap { min-height: 100vh; display: grid; place-items: center; padding: 16px; background: var(--mat-sys-surface-container-low); }
    .card { padding: 24px; width: 100%; max-width: 400px; }
    .full { width: 100%; }
    .secondary { color: var(--mat-sys-on-surface-variant); }
  `
})
export class BackOfficeLoginPage {
  private readonly bo = inject(BackOfficeService);
  private readonly router = inject(Router);
  private readonly notify = inject(NotifyService);
  protected readonly i18n = inject(I18nService);
  protected readonly busy = signal(false);
  protected readonly form = inject(FormBuilder).nonNullable.group({ email: ['', Validators.required], password: ['', Validators.required] });

  protected submit(): void {
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.bo.login(v.email, v.password).subscribe({
      next: () => void this.router.navigate(['/backoffice']),
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }
}

@Component({
  selector: 'app-bo-shell',
  imports: [...SHARED, MatSidenavModule, MatToolbarModule, MatListModule, RouterOutlet, RouterLinkActive],
  template: `
    <mat-sidenav-container class="container">
      <mat-sidenav [mode]="handset() ? 'over' : 'side'" [opened]="!handset()" #nav class="nav">
        <div class="brand">{{ 'bo.title' | t }}</div>
        <mat-nav-list>
          @for (i of items; track i.link) {
            <a mat-list-item [routerLink]="i.link" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: i.exact }" (click)="handset() && nav.close()">
              <mat-icon matListItemIcon>{{ i.icon }}</mat-icon><span matListItemTitle>{{ i.label | t }}</span></a>
          }
        </mat-nav-list>
      </mat-sidenav>
      <mat-sidenav-content>
        <mat-toolbar class="bar">
          @if (handset()) { <button mat-icon-button (click)="nav.toggle()"><mat-icon>menu</mat-icon></button> }
          <span class="spacer"></span>
          <span class="who">{{ bo.session()?.displayName }}</span>
          <button mat-button (click)="i18n.toggle()">{{ 'app.language' | t }}</button>
          <button mat-icon-button (click)="bo.logout()"><mat-icon class="flip-rtl">logout</mat-icon></button>
        </mat-toolbar>
        <main class="main"><router-outlet /></main>
      </mat-sidenav-content>
    </mat-sidenav-container>
  `,
  styles: `
    .container { height: 100vh; }
    .nav { width: 240px; }
    .brand { font: var(--mat-sys-title-medium); padding: 20px 16px 8px; color: var(--mat-sys-primary); }
    .bar { position: sticky; top: 0; z-index: 2; background: var(--mat-sys-surface); border-block-end: 1px solid var(--mat-sys-outline-variant); }
    .spacer { flex: 1; }
    .who { font-size: 14px; margin-inline-end: 8px; }
    .main { padding: 16px; }
    .active { background: var(--mat-sys-secondary-container); }
  `
})
export class BackOfficeShell {
  protected readonly bo = inject(BackOfficeService);
  protected readonly i18n = inject(I18nService);
  protected readonly handset = toSignal(inject(BreakpointObserver).observe('(max-width: 900px)').pipe(map((r) => r.matches)), { initialValue: false });
  protected readonly items = [
    { icon: 'space_dashboard', label: 'bo.nav.dashboard', link: '/backoffice', exact: true },
    { icon: 'storefront', label: 'bo.nav.customers', link: '/backoffice/customers', exact: false },
    { icon: 'receipt_long', label: 'bo.nav.invoices', link: '/backoffice/invoices', exact: false },
    { icon: 'sell', label: 'bo.nav.plans', link: '/backoffice/plans', exact: false },
    { icon: 'auto_awesome', label: 'bo.nav.ai', link: '/backoffice/ai', exact: false },
    { icon: 'admin_panel_settings', label: 'bo.nav.admins', link: '/backoffice/admins', exact: false }
  ];
}

@Component({
  selector: 'app-bo-dashboard',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'bo.nav.dashboard' | t }}</h1>
      <span class="spacer"></span>
      <button mat-stroked-button (click)="runBilling()"><mat-icon>autorenew</mat-icon> {{ 'bo.runBilling' | t }}</button>
    </div>
    @if (s(); as x) {
      <div class="card-grid">
        <mat-card appearance="outlined" class="stat"><div class="label">{{ 'bo.mrr' | t }}</div><div class="value ltr">{{ x.monthlyRecurring | money }}</div></mat-card>
        <mat-card appearance="outlined" class="stat" routerLink="/backoffice/customers"><div class="label">{{ 'bo.customers' | t }}</div><div class="value">{{ x.customers }}</div>
          <div class="hint">{{ 'bo.status.Active' | t }} {{ x.active }} · {{ 'bo.status.Trial' | t }} {{ x.trial }}</div></mat-card>
        <mat-card appearance="outlined" class="stat warn" [routerLink]="['/backoffice/invoices']" [queryParams]="{ status: 'overdue' }">
          <div class="label">{{ 'bo.overdue' | t }}</div><div class="value ltr">{{ x.overdue | money }}</div>
          <div class="hint">{{ 'bo.status.PastDue' | t }} {{ x.pastDue }} · {{ 'bo.status.Suspended' | t }} {{ x.suspended }}</div></mat-card>
        <mat-card appearance="outlined" class="stat"><div class="label">{{ 'bo.collected' | t }}</div><div class="value ltr">{{ x.collectedThisMonth | money }}</div></mat-card>
        <mat-card appearance="outlined" class="stat"><div class="label">{{ 'bo.openBalance' | t }}</div><div class="value ltr">{{ x.openBalance | money }}</div>
          <div class="hint">{{ 'bo.dueSoon' | t: { n: x.invoicesDueNext7Days } }}</div></mat-card>
      </div>
      <h2 class="section">{{ 'bo.byType' | t }}</h2>
      <div class="types">
        @for (t of x.byBusinessType; track t.businessType) { <span class="chip">{{ 'bo.type.' + t.businessType | t }} · <b>{{ t.count }}</b></span> }
      </div>
    }
  `,
  styles: `.spacer { flex: 1; } .stat { cursor: pointer; } .stat.warn .value { color: #8c1d18; } .section { font: var(--mat-sys-title-medium); margin: 20px 0 8px; }
    .types { display: flex; flex-wrap: wrap; gap: 8px; }`
})
export class BackOfficeDashboardPage implements OnInit {
  private readonly bo = inject(BackOfficeService);
  private readonly notify = inject(NotifyService);
  private readonly i18n = inject(I18nService);
  protected readonly s = signal<BoSummary | null>(null);

  ngOnInit(): void {
    this.bo.get<BoSummary>('summary').subscribe({ next: (s) => this.s.set(s), error: (e) => this.notify.error(e) });
  }

  protected runBilling(): void {
    this.bo.post<{ issued: number }>('billing/run').subscribe({
      next: (r) => {
        this.notify.info(this.i18n.t('bo.billingDone', { n: r.issued }));
        this.ngOnInit();
      },
      error: (e) => this.notify.error(e)
    });
  }
}

@Component({
  selector: 'app-bo-customers',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'bo.nav.customers' | t }}</h1>
      <span class="spacer"></span>
      <button mat-flat-button (click)="create()"><mat-icon>add</mat-icon> {{ 'bo.newCustomer' | t }}</button>
    </div>
    <div class="toolbar">
      <mat-form-field><mat-label>{{ 'app.search' | t }}</mat-label><input matInput [formControl]="search" (keydown.enter)="load()" /></mat-form-field>
      <mat-form-field><mat-label>{{ 'app.status' | t }}</mat-label>
        <mat-select [formControl]="status" (selectionChange)="load()"><mat-option value="">{{ 'bo.all' | t }}</mat-option>
          @for (s of statuses; track s) { <mat-option [value]="s">{{ 'bo.status.' + s | t }}</mat-option> }
          <mat-option value="overdue">{{ 'bo.overdue' | t }}</mat-option></mat-select></mat-form-field>
      <button mat-button (click)="load()">{{ 'app.search' | t }}</button>
    </div>
    <div class="table-wrap">
      <table class="line-editor">
        <thead><tr><th>{{ 'bo.code' | t }}</th><th>{{ 'bo.customer' | t }}</th><th>{{ 'bo.businessType' | t }}</th><th>{{ 'bo.plan' | t }}</th>
          <th class="num">{{ 'bo.monthly' | t }}</th><th class="num">{{ 'bo.balance' | t }}</th><th>{{ 'app.status' | t }}</th><th>{{ 'bo.nextInvoice' | t }}</th></tr></thead>
        <tbody>
          @for (c of rows(); track c.id) {
            <tr class="clickable" [routerLink]="['/backoffice/customers', c.id]">
              <td class="ltr">{{ c.code }}</td>
              <td>{{ c | bi }}<div class="secondary">{{ c.contactName }} <span class="ltr">{{ c.contactPhone }}</span> · {{ c.area }}</div></td>
              <td>{{ c.businessType ? ('bo.type.' + c.businessType | t) : '' }}</td>
              <td class="ltr">{{ c.planCode }} <span class="secondary">{{ c.billingCycle ? ('bo.cycle.' + c.billingCycle | t) : '' }}</span></td>
              <td class="num ltr">{{ c.monthlyValue | money }}</td>
              <td class="num ltr" [class.bad]="c.overdue > 0">{{ c.balance | money }}</td>
              <td>
                @if (c.tenantStatus !== 'Active') { <span class="chip" [class]="tone(c.tenantStatus)">{{ 'bo.status.' + c.tenantStatus | t }}</span> }
                @else if (c.subscriptionStatus) { <span class="chip" [class]="tone(c.subscriptionStatus)">{{ 'bo.status.' + c.subscriptionStatus | t }}</span> }
              </td>
              <td class="ltr">{{ c.nextInvoiceOn | d }}</td>
            </tr>
          }
        </tbody>
      </table>
      @if (!rows().length) { <p class="secondary">{{ 'app.none' | t }}</p> }
    </div>
  `,
  styles: `.spacer { flex: 1; } .toolbar { display: flex; flex-wrap: wrap; gap: 8px; align-items: baseline; } .clickable { cursor: pointer; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 12px; } .bad { color: #8c1d18; font-weight: 600; }`
})
export class BackOfficeCustomersPage implements OnInit {
  private readonly bo = inject(BackOfficeService);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  private readonly notify = inject(NotifyService);
  protected readonly statuses = ['Active', 'Trial', 'PastDue', 'Suspended', 'Closed'];
  protected readonly rows = signal<CustomerRow[]>([]);
  protected readonly search = new FormControl('', { nonNullable: true });
  protected readonly status = new FormControl('', { nonNullable: true });
  protected readonly tone = (s: string) => STATUS_TONE[s] ?? '';

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.bo.get<CustomerRow[]>('customers', { search: this.search.value, status: this.status.value }).subscribe({
      next: (r) => this.rows.set(r),
      error: (e) => this.notify.error(e)
    });
  }

  protected create(): void {
    this.dialog.open(NewCustomerDialog, { width: '760px' }).afterClosed().pipe(filter(Boolean))
      .subscribe((d: CustomerDetail) => void this.router.navigate(['/backoffice/customers', d.customer.id]));
  }
}

@Component({
  selector: 'app-bo-new-customer',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'bo.newCustomer' | t }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field><mat-label>{{ 'bo.code' | t }}</mat-label><input matInput formControlName="code" dir="ltr" placeholder="al-noor-abayas" />
          <mat-hint>{{ 'bo.codeHint' | t }}</mat-hint></mat-form-field>
        <mat-form-field><mat-label>{{ 'bo.businessType' | t }}</mat-label>
          <mat-select formControlName="businessType">@for (b of types; track b) { <mat-option [value]="b">{{ 'bo.type.' + b | t }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>{{ 'bo.nameAr' | t }}</mat-label><input matInput formControlName="nameAr" dir="rtl" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'bo.nameEn' | t }}</mat-label><input matInput formControlName="nameEn" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'bo.contactName' | t }}</mat-label><input matInput formControlName="contactName" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'bo.contactPhone' | t }}</mat-label><input matInput formControlName="contactPhone" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'bo.area' | t }}</mat-label><input matInput formControlName="area" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'bo.crNo' | t }}</mat-label><input matInput formControlName="crNo" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'bo.plan' | t }}</mat-label>
          <mat-select formControlName="planCode">@for (p of plans(); track p.id) { <mat-option [value]="p.code">{{ p | bi }} · <span class="ltr">{{ p.monthlyPrice | money }}</span></mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>{{ 'bo.cycle' | t }}</mat-label>
          <mat-select formControlName="billingCycle"><mat-option value="Monthly">{{ 'bo.cycle.Monthly' | t }}</mat-option><mat-option value="Annual">{{ 'bo.cycle.Annual' | t }}</mat-option></mat-select></mat-form-field>
        <mat-form-field><mat-label>{{ 'bo.priceOverride' | t }}</mat-label><input matInput type="number" formControlName="price" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'bo.trialDays' | t }}</mat-label><input matInput type="number" formControlName="trialDays" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'bo.ownerEmail' | t }}</mat-label><input matInput formControlName="adminEmail" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'bo.ownerPassword' | t }}</mat-label><input matInput formControlName="adminPassword" dir="ltr" />
          <mat-hint>{{ 'bo.passwordHint' | t }}</mat-hint></mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid || busy()" (click)="save()">{{ 'app.create' | t }}</button>
    </mat-dialog-actions>
  `
})
export class NewCustomerDialog implements OnInit {
  private readonly bo = inject(BackOfficeService);
  private readonly notify = inject(NotifyService);
  private readonly ref = inject(MatDialogRef<NewCustomerDialog, CustomerDetail>);
  protected readonly types = BUSINESS_TYPES;
  protected readonly plans = signal<BoPlan[]>([]);
  protected readonly busy = signal(false);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    code: ['', [Validators.required, Validators.pattern(/^[a-z0-9][a-z0-9-]{2,39}$/)]], businessType: ['retail'], nameAr: ['', Validators.required],
    nameEn: ['', Validators.required], contactName: [''], contactPhone: [''], area: ['Al Aziziya'], crNo: [''], planCode: ['SHOP', Validators.required],
    billingCycle: ['Monthly'], price: [null as number | null], trialDays: [14], adminEmail: ['', [Validators.required, Validators.email]],
    adminPassword: [this.password(), [Validators.required, Validators.minLength(10)]]
  });

  ngOnInit(): void {
    this.bo.get<BoPlan[]>('plans').subscribe((p) => this.plans.set(p.filter((x) => x.isActive)));
  }

  private password(): string {
    const a = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789';
    const bytes = crypto.getRandomValues(new Uint8Array(12));
    return Array.from(bytes, (b) => a[b % a.length]).join('');
  }

  protected save(): void {
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.bo.post<CustomerDetail>('customers', {
      ...v, price: v.price === null || (v.price as unknown) === '' ? null : Number(v.price), trialDays: Number(v.trialDays) || 0,
      adminNameEn: v.contactName || 'Owner', adminNameAr: v.contactName || 'المالك', crNo: v.crNo || null
    }).subscribe({
      next: (d) => this.ref.close(d),
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }
}

@Component({
  selector: 'app-bo-customer',
  imports: [...SHARED],
  template: `
    @if (d(); as x) {
      <div class="page-header">
        <a mat-icon-button routerLink="/backoffice/customers"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
        <h1>{{ x.customer | bi }} <span class="ltr secondary">{{ x.customer.code }}</span></h1>
        <span class="chip" [class]="tone(x.customer.tenantStatus === 'Active' ? (x.customer.subscriptionStatus ?? '') : x.customer.tenantStatus)">
          {{ 'bo.status.' + (x.customer.tenantStatus === 'Active' ? x.customer.subscriptionStatus : x.customer.tenantStatus) | t }}</span>
        <span class="spacer"></span>
        @if (x.customer.tenantStatus === 'Active') { <button mat-stroked-button (click)="act('suspend')">{{ 'bo.suspend' | t }}</button> }
        @else { <button mat-stroked-button (click)="act('reactivate')">{{ 'bo.reactivate' | t }}</button> }
        <button mat-button [matMenuTriggerFor]="more"><mat-icon>more_vert</mat-icon></button>
        <mat-menu #more="matMenu">
          <button mat-menu-item (click)="resetPassword()">{{ 'bo.resetOwner' | t }}</button>
          <button mat-menu-item (click)="act('close')">{{ 'bo.close' | t }}</button>
        </mat-menu>
      </div>
      <div class="card-grid">
        <mat-card appearance="outlined" class="stat"><div class="label">{{ 'bo.balance' | t }}</div><div class="value ltr">{{ x.customer.balance | money }}</div>
          @if (x.customer.overdue > 0) { <div class="hint bad">{{ 'bo.overdue' | t }} <span class="ltr">{{ x.customer.overdue | money }}</span></div> }</mat-card>
        <mat-card appearance="outlined" class="stat"><div class="label">{{ 'bo.monthly' | t }}</div><div class="value ltr">{{ x.customer.monthlyValue | money }}</div></mat-card>
        <mat-card appearance="outlined" class="stat"><div class="label">{{ 'bo.users' | t }}</div><div class="value">{{ x.usage.activeUsers }}</div>
          <div class="hint">{{ 'bo.lastLogin' | t }} <span class="ltr">{{ x.usage.lastLoginAt ? (x.usage.lastLoginAt.slice(0, 10) | d) : '—' }}</span></div></mat-card>
        <mat-card appearance="outlined" class="stat"><div class="label">{{ 'bo.owner' | t }}</div><div class="value small ltr">{{ x.usage.ownerEmail }}</div>
          <div class="hint">{{ 'bo.loginWith' | t }} <b class="ltr">{{ x.customer.code }}</b></div></mat-card>
      </div>
      <mat-tab-group mat-stretch-tabs="false">
        <mat-tab [label]="'bo.subscription' | t">
          <div class="tab-body">
            <form [formGroup]="sub" class="form-grid">
              <mat-form-field><mat-label>{{ 'bo.plan' | t }}</mat-label>
                <mat-select formControlName="planId" (selectionChange)="planChanged()">@for (p of plans(); track p.id) { <mat-option [value]="p.id">{{ p | bi }}</mat-option> }</mat-select></mat-form-field>
              <mat-form-field><mat-label>{{ 'bo.cycle' | t }}</mat-label>
                <mat-select formControlName="billingCycle"><mat-option value="Monthly">{{ 'bo.cycle.Monthly' | t }}</mat-option><mat-option value="Annual">{{ 'bo.cycle.Annual' | t }}</mat-option></mat-select></mat-form-field>
              <mat-form-field><mat-label>{{ 'bo.price' | t }}</mat-label><input matInput type="number" formControlName="price" dir="ltr" /></mat-form-field>
              <mat-form-field><mat-label>{{ 'bo.nextInvoice' | t }}</mat-label><input matInput type="date" formControlName="nextInvoiceOn" /></mat-form-field>
              <mat-form-field><mat-label>{{ 'bo.trialEnds' | t }}</mat-label><input matInput type="date" formControlName="trialEndsOn" /></mat-form-field>
              <mat-form-field><mat-label>{{ 'bo.graceDays' | t }}</mat-label><input matInput type="number" formControlName="graceDays" dir="ltr" /></mat-form-field>
              <mat-form-field><mat-label>{{ 'bo.maxUsers' | t }}</mat-label><input matInput type="number" formControlName="maxUsers" dir="ltr" /></mat-form-field>
              <mat-form-field><mat-label>{{ 'bo.maxEmployees' | t }}</mat-label><input matInput type="number" formControlName="maxEmployees" dir="ltr" /></mat-form-field>
              <mat-slide-toggle formControlName="autoSuspend">{{ 'bo.autoSuspend' | t }}</mat-slide-toggle>
            </form>
            <h3>{{ 'bo.modules' | t }}</h3>
            <p class="secondary">{{ 'bo.modulesHint' | t }}</p>
            <div class="modules">
              @for (m of allModules(); track m.code) {
                <mat-checkbox [checked]="selected().has(m.code)" (change)="toggleModule(m, $event.checked)">{{ m | bi }}</mat-checkbox>
              }
            </div>
            <button mat-flat-button (click)="saveSubscription()">{{ 'app.save' | t }}</button>
          </div>
        </mat-tab>
        <mat-tab [label]="'bo.billing' | t">
          <div class="tab-body">
            <div class="actions">
              <button mat-flat-button (click)="pay()"><mat-icon>payments</mat-icon> {{ 'bo.recordPayment' | t }}</button>
              <button mat-stroked-button (click)="invoice()"><mat-icon>add</mat-icon> {{ 'bo.manualInvoice' | t }}</button>
            </div>
            <h3>{{ 'bo.nav.invoices' | t }}</h3>
            <table class="line-editor">
              <thead><tr><th>{{ 'bo.invoiceNo' | t }}</th><th>{{ 'bo.period' | t }}</th><th>{{ 'bo.description' | t }}</th><th class="num">{{ 'app.amount' | t }}</th>
                <th class="num">{{ 'bo.paid' | t }}</th><th>{{ 'bo.due' | t }}</th><th>{{ 'app.status' | t }}</th><th></th></tr></thead>
              <tbody>
                @for (i of x.invoices; track i.id) {
                  <tr><td class="ltr">{{ i.invoiceNo }}</td><td class="ltr">{{ i.periodStart | d }} – {{ i.periodEnd | d }}</td><td>{{ i.description }}</td>
                    <td class="num ltr">{{ i.amount | money }}</td><td class="num ltr">{{ i.paidAmount | money }}</td>
                    <td class="ltr" [class.bad]="i.overdue">{{ i.dueDate | d }}</td>
                    <td><span class="chip" [class]="tone(i.overdue ? 'PastDue' : i.status)">{{ (i.overdue ? 'bo.overdue' : 'bo.inv.' + i.status) | t }}</span></td>
                    <td>@if (i.status === 'Open' && i.paidAmount === 0) { <button mat-button (click)="voidInvoice(i)">{{ 'bo.void' | t }}</button> }</td></tr>
                }
              </tbody>
            </table>
            <h3>{{ 'bo.payments' | t }}</h3>
            <table class="line-editor">
              <thead><tr><th>{{ 'app.date' | t }}</th><th class="num">{{ 'app.amount' | t }}</th><th>{{ 'bo.method' | t }}</th><th>{{ 'bo.reference' | t }}</th>
                <th>{{ 'bo.invoiceNo' | t }}</th><th>{{ 'bo.recordedBy' | t }}</th></tr></thead>
              <tbody>
                @for (p of x.payments; track p.id) {
                  <tr><td class="ltr">{{ p.paidOn | d }}</td><td class="num ltr">{{ p.amount | money }}</td><td>{{ 'bo.method.' + p.method | t }}</td>
                    <td class="ltr">{{ p.reference }}</td><td class="ltr">{{ p.invoiceNo }}</td><td>{{ p.recordedByName }}</td></tr>
                }
              </tbody>
            </table>
          </div>
        </mat-tab>
        <mat-tab [label]="'bo.details' | t">
          <form [formGroup]="info" class="form-grid tab-body">
            <mat-form-field><mat-label>{{ 'bo.nameAr' | t }}</mat-label><input matInput formControlName="nameAr" dir="rtl" /></mat-form-field>
            <mat-form-field><mat-label>{{ 'bo.nameEn' | t }}</mat-label><input matInput formControlName="nameEn" dir="ltr" /></mat-form-field>
            <mat-form-field><mat-label>{{ 'bo.businessType' | t }}</mat-label>
              <mat-select formControlName="businessType">@for (b of types; track b) { <mat-option [value]="b">{{ 'bo.type.' + b | t }}</mat-option> }</mat-select></mat-form-field>
            <mat-form-field><mat-label>{{ 'bo.contactName' | t }}</mat-label><input matInput formControlName="contactName" /></mat-form-field>
            <mat-form-field><mat-label>{{ 'bo.contactPhone' | t }}</mat-label><input matInput formControlName="contactPhone" dir="ltr" /></mat-form-field>
            <mat-form-field><mat-label>{{ 'bo.contactEmail' | t }}</mat-label><input matInput formControlName="contactEmail" dir="ltr" /></mat-form-field>
            <mat-form-field><mat-label>{{ 'bo.area' | t }}</mat-label><input matInput formControlName="area" /></mat-form-field>
            <mat-form-field class="wide"><mat-label>{{ 'app.notes' | t }}</mat-label><textarea matInput rows="3" formControlName="notes"></textarea></mat-form-field>
            <div><button mat-flat-button type="button" (click)="saveInfo()">{{ 'app.save' | t }}</button></div>
          </form>
        </mat-tab>
      </mat-tab-group>
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: `
    .spacer { flex: 1; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 13px; }
    .tab-body { margin-block-start: 16px; }
    .modules { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 4px 12px; margin-block-end: 12px; }
    .actions { display: flex; gap: 8px; flex-wrap: wrap; }
    .bad { color: #8c1d18; }
    .value.small { font-size: 14px; }
    .wide { grid-column: 1 / -1; }
  `
})
export class BackOfficeCustomerPage implements OnInit {
  readonly id = input.required<string>();
  private readonly bo = inject(BackOfficeService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly i18n = inject(I18nService);
  private readonly fb = inject(FormBuilder).nonNullable;
  protected readonly types = BUSINESS_TYPES;
  protected readonly d = signal<CustomerDetail | null>(null);
  protected readonly plans = signal<BoPlan[]>([]);
  protected readonly allModules = computed(() => this.d()?.modules ?? []);
  protected readonly selected = signal(new Set<string>());
  protected readonly tone = (s: string) => STATUS_TONE[s] ?? '';
  protected readonly sub = this.fb.group({
    planId: [''], billingCycle: ['Monthly'], price: [0], nextInvoiceOn: [''], trialEndsOn: [''], graceDays: [14], maxUsers: [null as number | null],
    maxEmployees: [null as number | null], autoSuspend: [true]
  });
  protected readonly info = this.fb.group({
    nameAr: [''], nameEn: [''], businessType: [''], contactName: [''], contactPhone: [''], contactEmail: [''], area: [''], notes: ['']
  });

  ngOnInit(): void {
    this.bo.get<BoPlan[]>('plans').subscribe((p) => this.plans.set(p));
    this.load();
  }

  private load(): void {
    this.bo.get<CustomerDetail>(`customers/${this.id()}`).subscribe({ next: (d) => this.show(d), error: (e) => this.notify.error(e) });
  }

  private show(d: CustomerDetail): void {
    this.d.set(d);
    const s = d.subscription;
    this.selected.set(new Set(s?.modules ?? d.modules.filter((m) => m.enabled).map((m) => m.code)));
    if (s) this.sub.reset({
      planId: s.planId, billingCycle: s.billingCycle, price: s.price, nextInvoiceOn: s.nextInvoiceOn, trialEndsOn: s.trialEndsOn ?? '',
      graceDays: s.graceDays, maxUsers: s.maxUsers, maxEmployees: s.maxEmployees, autoSuspend: s.autoSuspend
    });
    const c = d.customer;
    this.info.reset({ nameAr: c.nameAr, nameEn: c.nameEn, businessType: c.businessType ?? '', contactName: c.contactName ?? '', contactPhone: c.contactPhone ?? '',
      contactEmail: '', area: c.area ?? '', notes: d.notes ?? '' });
  }

  protected planChanged(): void {
    const plan = this.plans().find((p) => p.id === this.sub.value.planId);
    if (!plan) return;
    this.selected.set(new Set(plan.modules));
    this.sub.patchValue({ price: this.sub.value.billingCycle === 'Annual' ? plan.annualPrice : plan.monthlyPrice, maxUsers: plan.maxUsers,
      maxEmployees: plan.maxEmployees });
  }

  protected toggleModule(m: BoModule, on: boolean): void {
    this.selected.update((set) => {
      const next = new Set(set);
      if (on) {
        next.add(m.code);
        m.requires.forEach((r) => next.add(r));
      } else {
        next.delete(m.code);
        // Drop the modules that depend on it.
        this.allModules().filter((x) => x.requires.includes(m.code)).forEach((x) => next.delete(x.code));
      }
      return next;
    });
  }

  protected saveSubscription(): void {
    const v = this.sub.getRawValue();
    this.bo.put<CustomerDetail>(`customers/${this.id()}/subscription`, {
      planId: v.planId, modules: [...this.selected()], billingCycle: v.billingCycle, price: Number(v.price) || 0, autoSuspend: v.autoSuspend,
      graceDays: Number(v.graceDays) || 0, maxUsers: v.maxUsers === null || (v.maxUsers as unknown) === '' ? null : Number(v.maxUsers),
      maxEmployees: v.maxEmployees === null || (v.maxEmployees as unknown) === '' ? null : Number(v.maxEmployees), nextInvoiceOn: v.nextInvoiceOn || null,
      trialEndsOn: v.trialEndsOn || null, notes: null
    }).subscribe({ next: (d) => { this.show(d); this.notify.success(); }, error: (e) => this.notify.error(e) });
  }

  protected saveInfo(): void {
    const v = this.info.getRawValue();
    this.bo.put<CustomerDetail>(`customers/${this.id()}`, { ...v, contactEmail: v.contactEmail || null })
      .subscribe({ next: (d) => { this.show(d); this.notify.success(); }, error: (e) => this.notify.error(e) });
  }

  protected act(action: 'suspend' | 'reactivate' | 'close'): void {
    if (!confirm(this.i18n.t('bo.confirm.' + action))) return;
    this.bo.post<CustomerDetail>(`customers/${this.id()}/${action}`).subscribe({ next: (d) => this.show(d), error: (e) => this.notify.error(e) });
  }

  protected resetPassword(): void {
    if (!confirm(this.i18n.t('bo.confirm.reset'))) return;
    this.bo.post<{ login: string; temporaryPassword: string }>(`customers/${this.id()}/reset-owner-password`).subscribe({
      next: (r) => prompt(this.i18n.t('bo.tempPassword', { login: r.login }), r.temporaryPassword),
      error: (e) => this.notify.error(e)
    });
  }

  protected pay(): void {
    const d = this.d();
    if (!d) return;
    this.dialog.open(PaymentDialog, { data: d, width: '480px' }).afterClosed().pipe(filter(Boolean))
      .subscribe((x: CustomerDetail) => { this.show(x); this.notify.success(); });
  }

  protected invoice(): void {
    const description = prompt(this.i18n.t('bo.invoiceDescription'));
    if (!description) return;
    const amount = Number(prompt(this.i18n.t('app.amount')));
    if (!amount) return;
    this.bo.post<CustomerDetail>(`customers/${this.id()}/invoices`, { description, amount }).subscribe({ next: (d) => this.show(d), error: (e) => this.notify.error(e) });
  }

  protected voidInvoice(i: BoInvoice): void {
    if (!confirm(this.i18n.t('bo.confirm.void'))) return;
    this.bo.post(`invoices/${i.id}/void`).subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }
}

@Component({
  selector: 'app-bo-payment',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'bo.recordPayment' | t }}</h2>
    <mat-dialog-content>
      <p class="secondary">{{ 'bo.balance' | t }} <b class="ltr">{{ data.customer.balance | money }}</b></p>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field><mat-label>{{ 'app.amount' | t }}</mat-label><input matInput type="number" formControlName="amount" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'app.date' | t }}</mat-label><input matInput type="date" formControlName="paidOn" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'bo.method' | t }}</mat-label>
          <mat-select formControlName="method">@for (m of methods; track m) { <mat-option [value]="m">{{ 'bo.method.' + m | t }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>{{ 'bo.reference' | t }}</mat-label><input matInput formControlName="reference" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'bo.invoiceNo' | t }}</mat-label>
          <mat-select formControlName="invoiceId"><mat-option value="">{{ 'bo.oldestFirst' | t }}</mat-option>
            @for (i of openInvoices; track i.id) { <mat-option [value]="i.id"><span class="ltr">{{ i.invoiceNo }} · {{ i.balance | money }}</span></mat-option> }</mat-select></mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid" (click)="save()">{{ 'app.save' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `.secondary { color: var(--mat-sys-on-surface-variant); }`
})
export class PaymentDialog {
  protected readonly data = inject<CustomerDetail>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<PaymentDialog, CustomerDetail>);
  private readonly bo = inject(BackOfficeService);
  private readonly notify = inject(NotifyService);
  protected readonly methods = ['BankTransfer', 'Cash', 'Card', 'Cheque', 'Fawran', 'Other'];
  protected readonly openInvoices = this.data.invoices.filter((i) => i.status === 'Open');
  protected readonly form = inject(FormBuilder).nonNullable.group({
    amount: [this.data.customer.balance, [Validators.required, Validators.min(0.01)]], paidOn: [todayIso(), Validators.required], method: ['BankTransfer'],
    reference: [''], invoiceId: ['']
  });

  protected save(): void {
    const v = this.form.getRawValue();
    this.bo.post<CustomerDetail>(`customers/${this.data.customer.id}/payments`, {
      amount: Number(v.amount), paidOn: v.paidOn, method: v.method, reference: v.reference || null, invoiceId: v.invoiceId || null, notes: null
    }).subscribe({ next: (d) => this.ref.close(d), error: (e) => this.notify.error(e) });
  }
}

@Component({
  selector: 'app-bo-invoices',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'bo.nav.invoices' | t }}</h1></div>
    <mat-form-field><mat-label>{{ 'app.status' | t }}</mat-label>
      <mat-select [formControl]="status" (selectionChange)="load()">
        <mat-option value="">{{ 'bo.all' | t }}</mat-option><mat-option value="open">{{ 'bo.inv.Open' | t }}</mat-option>
        <mat-option value="overdue">{{ 'bo.overdue' | t }}</mat-option><mat-option value="paid">{{ 'bo.inv.Paid' | t }}</mat-option></mat-select></mat-form-field>
    <div class="table-wrap">
      <table class="line-editor">
        <thead><tr><th>{{ 'bo.invoiceNo' | t }}</th><th>{{ 'bo.customer' | t }}</th><th>{{ 'bo.description' | t }}</th><th class="num">{{ 'app.amount' | t }}</th>
          <th class="num">{{ 'bo.balance' | t }}</th><th>{{ 'bo.due' | t }}</th><th>{{ 'app.status' | t }}</th></tr></thead>
        <tbody>
          @for (i of rows(); track i.id) {
            <tr class="clickable" [routerLink]="['/backoffice/customers', i.tenantId]">
              <td class="ltr">{{ i.invoiceNo }}</td><td>{{ i | bi:'customerName' }} <span class="ltr secondary">{{ i.customerCode }}</span></td>
              <td>{{ i.description }}</td><td class="num ltr">{{ i.amount | money }}</td><td class="num ltr">{{ i.balance | money }}</td>
              <td class="ltr" [class.bad]="i.overdue">{{ i.dueDate | d }}</td>
              <td><span class="chip" [class]="tone(i.overdue ? 'PastDue' : i.status)">{{ (i.overdue ? 'bo.overdue' : 'bo.inv.' + i.status) | t }}</span></td>
            </tr>
          }
        </tbody>
      </table>
      @if (!rows().length) { <p class="secondary">{{ 'app.none' | t }}</p> }
    </div>
  `,
  styles: `.clickable { cursor: pointer; } .secondary { color: var(--mat-sys-on-surface-variant); font-size: 12px; } .bad { color: #8c1d18; }`
})
export class BackOfficeInvoicesPage implements OnInit {
  private readonly bo = inject(BackOfficeService);
  private readonly notify = inject(NotifyService);
  private readonly router = inject(Router);
  protected readonly rows = signal<BoInvoice[]>([]);
  protected readonly status = new FormControl(new URLSearchParams(location.search).get('status') ?? 'open', { nonNullable: true });
  protected readonly tone = (s: string) => STATUS_TONE[s] ?? '';

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.bo.get<BoInvoice[]>('invoices', { status: this.status.value }).subscribe({ next: (r) => this.rows.set(r), error: (e) => this.notify.error(e) });
  }
}

@Component({
  selector: 'app-bo-plans',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'bo.nav.plans' | t }}</h1></div>
    <div class="plans">
      @for (p of plans(); track p.id) {
        <mat-card appearance="outlined" class="plan">
          <h3>{{ p | bi }} <span class="ltr secondary">{{ p.code }}</span></h3>
          <div class="form-grid">
            <mat-form-field><mat-label>{{ 'bo.cycle.Monthly' | t }}</mat-label><input matInput type="number" dir="ltr" [formControl]="price(p, 'monthly')" /></mat-form-field>
            <mat-form-field><mat-label>{{ 'bo.cycle.Annual' | t }}</mat-label><input matInput type="number" dir="ltr" [formControl]="price(p, 'annual')" /></mat-form-field>
          </div>
          <div class="modules">
            @for (m of modules(); track m.code) {
              <mat-checkbox [checked]="p.modules.includes(m.code)" (change)="toggle(p, m.code, $event.checked)">{{ m | bi }}</mat-checkbox>
            }
          </div>
          <div class="foot"><span class="secondary">{{ 'bo.customers' | t }}: {{ p.customers }}</span>
            <button mat-stroked-button (click)="save(p)">{{ 'app.save' | t }}</button></div>
        </mat-card>
      }
    </div>
  `,
  styles: `
    .plans { display: grid; grid-template-columns: repeat(auto-fill, minmax(320px, 1fr)); gap: 16px; }
    .plan { padding: 16px; }
    .modules { display: grid; grid-template-columns: 1fr 1fr; gap: 0 8px; }
    .foot { display: flex; justify-content: space-between; align-items: center; margin-top: 8px; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 13px; }
  `
})
export class BackOfficePlansPage implements OnInit {
  private readonly bo = inject(BackOfficeService);
  private readonly notify = inject(NotifyService);
  protected readonly plans = signal<BoPlan[]>([]);
  protected readonly modules = signal<{ code: string; nameEn: string; nameAr: string }[]>([]);
  private readonly prices = new Map<string, FormControl<number>>();

  ngOnInit(): void {
    this.bo.get<{ code: string; nameEn: string; nameAr: string }[]>('modules').subscribe((m) => this.modules.set(m));
    this.bo.get<BoPlan[]>('plans').subscribe((p) => this.plans.set(p));
  }

  protected price(p: BoPlan, kind: 'monthly' | 'annual'): FormControl<number> {
    const key = `${p.id}:${kind}`;
    if (!this.prices.has(key)) this.prices.set(key, new FormControl(kind === 'monthly' ? p.monthlyPrice : p.annualPrice, { nonNullable: true }));
    return this.prices.get(key)!;
  }

  protected toggle(p: BoPlan, code: string, on: boolean): void {
    p.modules = on ? [...p.modules, code] : p.modules.filter((m) => m !== code);
  }

  protected save(p: BoPlan): void {
    this.bo.put(`plans/${p.id}`, {
      code: p.code, nameEn: p.nameEn, nameAr: p.nameAr, monthlyPrice: Number(this.price(p, 'monthly').value), annualPrice: Number(this.price(p, 'annual').value),
      modules: p.modules, maxUsers: p.maxUsers, maxEmployees: p.maxEmployees, isActive: p.isActive
    }).subscribe({ next: () => this.notify.success(), error: (e) => this.notify.error(e) });
  }
}

@Component({
  selector: 'app-bo-admins',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'bo.nav.admins' | t }}</h1></div>
    <table class="line-editor">
      <thead><tr><th>{{ 'bo.email' | t }}</th><th>{{ 'bo.name' | t }}</th><th>{{ 'bo.lastLogin' | t }}</th></tr></thead>
      <tbody>@for (a of admins(); track a.id) { <tr><td class="ltr">{{ a.email }}</td><td>{{ a.displayName }}</td>
        <td class="ltr">{{ a.lastLoginAt ? (a.lastLoginAt.slice(0, 10) | d) : '—' }}</td></tr> }</tbody>
    </table>
    <h3>{{ 'bo.addAdmin' | t }}</h3>
    <form [formGroup]="form" class="form-grid">
      <mat-form-field><mat-label>{{ 'bo.email' | t }}</mat-label><input matInput formControlName="email" dir="ltr" /></mat-form-field>
      <mat-form-field><mat-label>{{ 'bo.name' | t }}</mat-label><input matInput formControlName="displayName" /></mat-form-field>
      <mat-form-field><mat-label>{{ 'bo.password' | t }}</mat-label><input matInput formControlName="password" dir="ltr" /></mat-form-field>
      <div><button mat-flat-button type="button" [disabled]="form.invalid" (click)="add()">{{ 'app.save' | t }}</button></div>
    </form>
  `
})
export class BackOfficeAdminsPage implements OnInit {
  private readonly bo = inject(BackOfficeService);
  private readonly notify = inject(NotifyService);
  protected readonly admins = signal<{ id: string; email: string; displayName: string; lastLoginAt: string | null }[]>([]);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    email: ['', [Validators.required, Validators.email]], displayName: ['', Validators.required], password: ['', [Validators.required, Validators.minLength(10)]]
  });

  ngOnInit(): void {
    this.bo.get<{ id: string; email: string; displayName: string; lastLoginAt: string | null }[]>('admins').subscribe((a) => this.admins.set(a));
  }

  protected add(): void {
    this.bo.post('admins', this.form.getRawValue()).subscribe({
      next: () => {
        this.notify.success();
        this.form.reset();
        this.ngOnInit();
      },
      error: (e) => this.notify.error(e)
    });
  }
}
