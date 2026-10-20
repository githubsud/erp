import { Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { forkJoin, of, catchError } from 'rxjs';
import { AiService, AiSummary } from '../../core/ai.service';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { daysBetween, toIso, todayIso } from '../../core/dates';
import { I18nService } from '../../core/i18n/i18n.service';
import { ExpiringDocument, GratuityRow, LeaveRequest, Paged, Employee, PayrollRun } from '../../core/models';
import { SHARED } from '../../shared/shared';

interface DashboardTile {
  section: string;
  key: string;
  labelEn: string;
  labelAr: string;
  value: number;
  format: 'money' | 'count';
  link: string;
  hintEn: string | null;
  hintAr: string | null;
  tone: 'ok' | 'warn' | 'bad' | null;
}

@Component({
  selector: 'app-dashboard',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'dashboard.welcome' | t: { name: (auth.user() | bi:'displayName') } }}</h1>
    </div>
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (moduleOff(); as m) {
      <mat-card appearance="outlined" class="setup-banner">
        <mat-icon>extension_off</mat-icon>
        <span>{{ 'dashboard.moduleOff' | t }}</span>
        <a mat-button routerLink="/settings/modules">{{ 'nav.modules' | t }}</a>
      </mat-card>
    }
    @if (suggestSetup()) {
      <mat-card appearance="outlined" class="setup-banner">
        <mat-icon>rocket_launch</mat-icon>
        <span>{{ 'setup.banner' | t }}</span>
        <a mat-flat-button routerLink="/settings/setup">{{ 'setup.start' | t }}</a>
      </mat-card>
    }
    @if (aiSummary(); as s) {
      <mat-card appearance="outlined" class="ai-summary">
        <div class="ai-head"><mat-icon>auto_awesome</mat-icon> <b>{{ 'ai.summary.title' | t }}</b> <span class="ltr secondary">{{ s.date | d }}</span>
          <span class="grow"></span><a mat-button routerLink="/ai">{{ 'ai.askMore' | t }}</a></div>
        <div class="ai-text">{{ s.text }}</div>
      </mat-card>
    }

    @if (approvals() > 0) {
      <mat-card appearance="outlined" class="stat approvals" routerLink="/approvals">
        <div class="label">{{ 'dashboard.approvals' | t }}</div>
        <div class="value">{{ approvals() }}</div>
      </mat-card>
    }
    @for (s of sections(); track s.key) {
      <h2 class="section">{{ 'dashboard.section.' + s.key | t }}</h2>
      <div class="card-grid">
        @for (tile of s.tiles; track tile.key) {
          <mat-card appearance="outlined" class="stat" [routerLink]="tile.link" [class.warn]="tile.tone === 'warn'" [class.bad]="tile.tone === 'bad'">
            <div class="label">{{ tile | bi:'label' }}</div>
            <div class="value ltr">{{ tile.format === 'money' ? (tile.value | money) : tile.value }}</div>
            @if (tile.hintEn) { <div class="hint">{{ tile | bi:'hint' }}</div> }
          </mat-card>
        }
      </div>
    }
    @if (auth.can('hr.employees:read') || auth.can('hr.payroll:read')) { <h2 class="section">{{ 'dashboard.section.hr' | t }}</h2> }

    <div class="card-grid">
      @if (auth.can('hr.employees:read')) {
        <mat-card appearance="outlined" class="stat" routerLink="/employees">
          <div class="label">{{ 'dashboard.activeEmployees' | t }}</div>
          <div class="value">{{ activeEmployees() }}</div>
        </mat-card>
      }
      @if (auth.can('hr.payroll:read')) {
        <mat-card appearance="outlined" class="stat" [routerLink]="currentRun() ? ['/payroll', currentRun()!.id] : '/payroll'">
          <div class="label">{{ 'dashboard.currentPayroll' | t }}</div>
          <div class="value">
            @if (currentRun(); as run) {
              <span class="chip" [class.ok]="run.status === 'Paid'" [class.info]="run.status !== 'Paid'">{{ 'payroll.status.' + run.status | t }}</span>
            } @else {
              <span class="chip warn">{{ 'dashboard.notStarted' | t }}</span>
            }
          </div>
          <div class="hint">
            {{ 'dashboard.wpsDeadline' | t }}: {{ wpsDeadline() | d }} ·
            @if (wpsDaysLeft() >= 0) {
              {{ 'dashboard.daysLeft' | t: { n: wpsDaysLeft() } }}
            } @else {
              <strong class="late">{{ 'dashboard.overdue' | t: { n: -wpsDaysLeft() } }}</strong>
            }
          </div>
        </mat-card>
        <mat-card appearance="outlined" class="stat" routerLink="/gratuity">
          <div class="label">{{ 'dashboard.gratuity' | t }}</div>
          <div class="value ltr">{{ gratuityTotal() | money }}</div>
          <div class="hint">{{ 'app.qar' | t }}</div>
        </mat-card>
      }
      @if (auth.can('hr.employees:read')) {
        <mat-card appearance="outlined" class="stat" routerLink="/leave">
          <div class="label">{{ 'dashboard.pendingLeave' | t }}</div>
          <div class="value">{{ pendingLeave() }}</div>
        </mat-card>
      }
    </div>

    @if (auth.can('hr.employees:read')) {
      <div class="page-header">
        <h1 class="h2">{{ 'dashboard.expiring' | t }}</h1>
        <a mat-button routerLink="/documents">{{ 'dashboard.viewAll' | t }}</a>
      </div>
      <div class="table-wrap">
        @if (expiring().length === 0) {
          <div class="empty">{{ 'app.none' | t }}</div>
        } @else {
          <table mat-table [dataSource]="expiring().slice(0, 8)">
            <ng-container matColumnDef="owner">
              <th mat-header-cell *matHeaderCellDef>{{ 'document.owner' | t }}</th>
              <td mat-cell *matCellDef="let d">{{ d | bi:'ownerName' }}</td>
            </ng-container>
            <ng-container matColumnDef="type">
              <th mat-header-cell *matHeaderCellDef>{{ 'document.type' | t }}</th>
              <td mat-cell *matCellDef="let d">{{ 'docType.' + d.docType | t }}</td>
            </ng-container>
            <ng-container matColumnDef="expiry">
              <th mat-header-cell *matHeaderCellDef>{{ 'document.expiryDate' | t }}</th>
              <td mat-cell *matCellDef="let d"><span class="ltr">{{ d.expiryDate | d }}</span></td>
            </ng-container>
            <ng-container matColumnDef="status">
              <th mat-header-cell *matHeaderCellDef>{{ 'document.daysLeft' | t }}</th>
              <td mat-cell *matCellDef="let d">
                <span class="chip" [class.bad]="d.status === 'Expired'" [class.warn]="d.status === 'Expiring'">{{ d.daysLeft }}</span>
              </td>
            </ng-container>
            <tr mat-header-row *matHeaderRowDef="docColumns"></tr>
            <tr mat-row *matRowDef="let row; columns: docColumns"></tr>
          </table>
        }
      </div>
    }
  `,
  styles: `
    .setup-banner { display: flex; align-items: center; gap: 12px; padding: 14px 16px; margin-bottom: 16px; flex-wrap: wrap;
      background: var(--mat-sys-secondary-container); }
    .setup-banner span { flex: 1; min-width: 200px; }
    .ai-summary { padding: 12px 16px; margin-bottom: 16px; }
    .ai-head { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
    .ai-head mat-icon { color: var(--mat-sys-primary); }
    .ai-text { white-space: pre-line; line-height: 1.8; margin-top: 6px; }
    .grow { flex: 1; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 13px; }
    .stat { cursor: pointer; }
    .stat.warn .value { color: #8a5300; }
    .stat.bad .value { color: #8c1d18; }
    .approvals { max-width: 260px; margin-block-end: 8px; border-color: var(--mat-sys-primary); }
    .section { font: var(--mat-sys-title-medium); color: var(--mat-sys-on-surface-variant); margin: 20px 0 8px; }
    .h2 { font: var(--mat-sys-title-large) !important; }
    .late { color: #8c1d18; }
  `
})
export class DashboardPage {
  private readonly api = inject(ApiService);
  protected readonly auth = inject(AuthService);
  protected readonly i18n = inject(I18nService);

  protected readonly loading = signal(true);
  protected readonly activeEmployees = signal(0);
  protected readonly currentRun = signal<PayrollRun | null>(null);
  protected readonly expiring = signal<ExpiringDocument[]>([]);
  protected readonly pendingLeave = signal(0);
  protected readonly gratuityTotal = signal(0);
  protected readonly docColumns = ['owner', 'type', 'expiry', 'status'];
  protected readonly approvals = signal(0);
  /** An owner whose company has no items yet is offered the quick-setup wizard. */
  protected readonly suggestSetup = signal(false);
  protected readonly aiSummary = signal<AiSummary | null>(null);
  /** Set when a screen of a switched-off module was opened by its address. */
  protected readonly moduleOff = signal(inject(ActivatedRoute).snapshot.queryParamMap.get('off'));
  private readonly tiles = signal<DashboardTile[]>([]);
  protected readonly sections = computed(() => {
    const order = ['today', 'finance', 'sales', 'purchasing', 'inventory'];
    return order.map((key) => ({ key, tiles: this.tiles().filter((t) => t.section === key) })).filter((s) => s.tiles.length);
  });

  private readonly today = new Date();
  /** Wages for this month fall due on the 1st of next month; WPS deposit within 7 days. */
  protected readonly wpsDeadline = computed(
    () => this.currentRun()?.wpsDeadline ?? toIso(new Date(this.today.getFullYear(), this.today.getMonth() + 1, 8))
  );
  protected readonly wpsDaysLeft = computed(() => daysBetween(todayIso(), this.wpsDeadline()));

  constructor() {
    this.api.get<DashboardTile[]>('core/dashboard').pipe(catchError(() => of<DashboardTile[]>([]))).subscribe((t) => this.tiles.set(t));
    this.api.get<unknown[]>('approvals/inbox').pipe(catchError(() => of<unknown[]>([]))).subscribe((a) => this.approvals.set(a.length));
    const ai = inject(AiService);
    if (ai.enabled('ai.summary:view')) ai.latestSummary(this.i18n.lang()).pipe(catchError(() => of(null))).subscribe((x) => this.aiSummary.set(x));
    if (this.auth.can('core.company:manage'))
      this.api.get<Paged<unknown>>('inventory/items', { pageSize: 1 }).pipe(catchError(() => of(null))).subscribe((p) => this.suggestSetup.set(p?.total === 0));
    const canHr = this.auth.can('hr.employees:read');
    const canPayroll = this.auth.can('hr.payroll:read');
    const none = <T>(value: T) => of(value);
    forkJoin({
      employees: canHr ? this.api.employees({ status: 'active', pageSize: 1 }).pipe(catchError(() => none<Paged<Employee> | null>(null))) : none(null),
      runs: canPayroll ? this.api.payrollRuns().pipe(catchError(() => none<PayrollRun[]>([]))) : none<PayrollRun[]>([]),
      expiring: canHr ? this.api.expiringDocuments(60).pipe(catchError(() => none<ExpiringDocument[]>([]))) : none<ExpiringDocument[]>([]),
      leave: canHr ? this.api.leaveRequests({ status: 'pending' }).pipe(catchError(() => none<LeaveRequest[]>([]))) : none<LeaveRequest[]>([]),
      gratuity: canPayroll ? this.api.gratuityLiability().pipe(catchError(() => none<GratuityRow[]>([]))) : none<GratuityRow[]>([])
    }).subscribe((r) => {
      this.activeEmployees.set(r.employees?.total ?? 0);
      const y = this.today.getFullYear();
      const m = this.today.getMonth() + 1;
      this.currentRun.set(r.runs.find((x) => x.year === y && x.month === m && x.status !== 'Reversed') ?? null);
      this.expiring.set(r.expiring);
      this.pendingLeave.set(r.leave.length);
      this.gratuityTotal.set(r.gratuity.reduce((s, g) => s + g.liability, 0));
      this.loading.set(false);
    });
  }
}

