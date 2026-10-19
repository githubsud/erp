import { Component, inject, OnInit, signal } from '@angular/core';
import { FormControl } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { ModulesService, ModuleInfo } from '../../core/modules.service';
import { NotifyService } from '../../core/notify.service';
import { downloadCsv } from '../../shared/export';
import { SHARED } from '../../shared/shared';

interface AuditRow {
  occurredAt: string;
  userId: string;
  userName: string | null;
  entityType: string;
  entityId: string;
  action: string;
  changes: string;
}

interface AuditResult {
  items: AuditRow[];
  total: number;
  page: number;
  pageSize: number;
  entityTypes: string[];
}

interface ReviewRow {
  userId: string;
  login: string;
  displayNameEn: string;
  displayNameAr: string;
  isActive: boolean;
  roles: string[];
  permissionCount: number;
  isOwner: boolean;
  mfaEnabled: boolean;
  lastLoginAt: string | null;
  flags: string[];
}

/** Audit trail with filters, and the periodic user access review. */
@Component({
  selector: 'app-audit',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'audit.title' | t }}</h1></div>
    <mat-tab-group mat-stretch-tabs="false" (selectedIndexChange)="select($event)">
      <mat-tab [label]="'audit.trail' | t">
        <div class="toolbar-row tab-body">
          <mat-form-field>
            <mat-label>{{ 'audit.entity' | t }}</mat-label>
            <mat-select [formControl]="entityType" (selectionChange)="load(1)">
              <mat-option value="">{{ 'audit.allEntities' | t }}</mat-option>
              @for (e of entityTypes(); track e) { <mat-option [value]="e">{{ e }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'audit.action' | t }}</mat-label>
            <mat-select [formControl]="action" (selectionChange)="load(1)">
              <mat-option value="">{{ 'audit.allActions' | t }}</mat-option>
              @for (a of actions; track a) { <mat-option [value]="a">{{ a }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <mat-form-field><mat-label>{{ 'app.from' | t }}</mat-label><input matInput type="date" [formControl]="from" (change)="load(1)" /></mat-form-field>
          <mat-form-field><mat-label>{{ 'app.to' | t }}</mat-label><input matInput type="date" [formControl]="to" (change)="load(1)" /></mat-form-field>
        </div>
        @if (page(); as p) {
          <table class="line-editor">
            <thead><tr><th>{{ 'audit.when' | t }}</th><th>{{ 'audit.user' | t }}</th><th>{{ 'audit.entity' | t }}</th><th>{{ 'audit.action' | t }}</th>
              <th>{{ 'audit.changes' | t }}</th></tr></thead>
            <tbody>
              @for (r of p.items; track $index) {
                <tr>
                  <td class="ltr nowrap">{{ r.occurredAt.slice(0, 10) | d }} {{ r.occurredAt.slice(11, 16) }}</td>
                  <td>{{ r.userName ?? '—' }}</td>
                  <td class="ltr">{{ r.entityType }}</td>
                  <td><span class="chip" [class.ok]="r.action === 'Added'" [class.warn]="r.action === 'Modified'" [class.bad]="r.action === 'Deleted'">{{ r.action }}</span></td>
                  <td><code class="changes ltr">{{ short(r.changes) }}</code></td>
                </tr>
              }
            </tbody>
          </table>
          <div class="pager">
            <span class="secondary">{{ 'audit.count' | t: { n: p.total } }}</span>
            <button mat-icon-button [disabled]="p.page <= 1" (click)="load(p.page - 1)"><mat-icon class="flip-rtl">chevron_left</mat-icon></button>
            <span class="ltr">{{ p.page }}</span>
            <button mat-icon-button [disabled]="p.page * p.pageSize >= p.total" (click)="load(p.page + 1)"><mat-icon class="flip-rtl">chevron_right</mat-icon></button>
          </div>
        }
      </mat-tab>
      <mat-tab [label]="'audit.review' | t">
        <div class="tab-body">
          <p class="secondary">{{ 'audit.reviewHint' | t }}</p>
          <div class="actions"><button mat-stroked-button (click)="exportReview()"><mat-icon>download</mat-icon> {{ 'export.csv' | t }}</button></div>
          <table class="line-editor">
            <thead><tr><th>{{ 'users.name' | t }}</th><th>{{ 'users.roles' | t }}</th><th class="num">{{ 'audit.permissions' | t }}</th><th>{{ 'audit.mfa' | t }}</th>
              <th>{{ 'audit.lastLogin' | t }}</th><th>{{ 'audit.flags' | t }}</th></tr></thead>
            <tbody>
              @for (r of review(); track r.userId) {
                <tr [class.inactive]="!r.isActive">
                  <td>{{ r | bi:'displayName' }}<div class="secondary ltr">{{ r.login }}</div></td>
                  <td>@for (role of r.roles; track role) { <span class="chip info">{{ 'role.' + role | t }}</span> }</td>
                  <td class="num ltr">{{ r.isOwner ? ('audit.all' | t) : r.permissionCount }}</td>
                  <td><mat-icon>{{ r.mfaEnabled ? 'verified_user' : 'remove_moderator' }}</mat-icon></td>
                  <td class="ltr">{{ r.lastLoginAt ? (r.lastLoginAt.slice(0, 10) | d) : '—' }}</td>
                  <td>@for (f of r.flags; track f) { <span class="chip bad">{{ 'audit.flag.' + f | t }}</span> }
                    @if (!r.isActive) { <span class="chip">{{ 'audit.inactive' | t }}</span> }</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      </mat-tab>
    </mat-tab-group>
  `,
  styles: `
    .tab-body { margin-block-start: 16px; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 12px; }
    .changes { font-size: 11px; white-space: pre-wrap; word-break: break-all; display: block; max-width: 520px; }
    .nowrap { white-space: nowrap; }
    .pager { display: flex; align-items: center; justify-content: flex-end; gap: 8px; margin-top: 8px; }
    .actions { display: flex; justify-content: flex-end; margin-bottom: 8px; }
    tr.inactive { opacity: 0.6; }
  `
})
export class AuditPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly actions = ['Added', 'Modified', 'Deleted'];
  protected readonly entityType = new FormControl('', { nonNullable: true });
  protected readonly action = new FormControl('', { nonNullable: true });
  protected readonly from = new FormControl('', { nonNullable: true });
  protected readonly to = new FormControl('', { nonNullable: true });
  protected readonly page = signal<AuditResult | null>(null);
  protected readonly entityTypes = signal<string[]>([]);
  protected readonly review = signal<ReviewRow[]>([]);

  ngOnInit(): void {
    this.load(1);
  }

  protected select(index: number): void {
    if (index === 1 && !this.review().length)
      this.api.get<ReviewRow[]>('core/access-review').subscribe({ next: (r) => this.review.set(r), error: (e) => this.notify.error(e) });
  }

  protected load(page: number): void {
    this.api
      .get<AuditResult>('core/audit-log', { entityType: this.entityType.value, action: this.action.value, from: this.from.value, to: this.to.value, page })
      .subscribe({
        next: (p) => {
          this.page.set(p);
          this.entityTypes.set(p.entityTypes);
        },
        error: (e) => this.notify.error(e)
      });
  }

  protected short(changes: string): string {
    return changes.length > 300 ? `${changes.slice(0, 300)}…` : changes;
  }

  protected exportReview(): void {
    downloadCsv('access_review.csv', ['User', 'Login', 'Roles', 'Permissions', 'MFA', 'Last login', 'Flags', 'Active'],
      this.review().map((r) => [r.displayNameEn, r.login, r.roles.join(' '), r.isOwner ? 'all' : r.permissionCount, r.mfaEnabled ? 'yes' : 'no',
        r.lastLoginAt?.slice(0, 10) ?? '', r.flags.join(' '), r.isActive ? 'yes' : 'no']));
  }
}

/** Switch optional modules on or off for the company. */
@Component({
  selector: 'app-modules',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'modules.title' | t }}</h1></div>
    <p class="secondary">{{ 'modules.hint' | t }}</p>
    <mat-card appearance="outlined" class="card">
      @for (m of modules(); track m.code) {
        <div class="row">
          <mat-slide-toggle [checked]="m.enabled" [disabled]="!canManage || busy() || m.licensed === false" (change)="toggle(m, $event.checked)">{{ m | bi }}</mat-slide-toggle>
          @if (m.licensed === false) { <span class="chip">{{ 'modules.notLicensed' | t }}</span> }
          @if (m.requires.length) { <span class="secondary">{{ 'modules.requires' | t }} @for (r of m.requires; track r) { {{ name(r) }} }</span> }
        </div>
      }
    </mat-card>
  `,
  styles: `
    .card { padding: 16px; max-width: 640px; }
    .row { display: flex; align-items: center; gap: 16px; padding: 10px 0; flex-wrap: wrap; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 13px; }
  `
})
export class ModulesPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly service = inject(ModulesService);
  private readonly notify = inject(NotifyService);
  protected readonly canManage = inject(AuthService).can('core.company:manage');
  protected readonly modules = signal<ModuleInfo[]>([]);
  protected readonly busy = signal(false);

  ngOnInit(): void {
    this.api.get<ModuleInfo[]>('core/modules').subscribe((m) => this.modules.set(m));
  }

  protected name(code: string): string {
    const m = this.modules().find((x) => x.code === code);
    return m ? m.nameEn : code;
  }

  protected toggle(m: ModuleInfo, on: boolean): void {
    const disabled = this.modules().filter((x) => x.licensed !== false && (x.code === m.code ? !on : !x.enabled)).map((x) => x.code);
    this.busy.set(true);
    this.api.put<ModuleInfo[]>('core/modules', { disabled }).subscribe({
      next: (list) => {
        this.busy.set(false);
        this.modules.set(list);
        this.service.set(list);
        this.notify.success();
      },
      error: (e) => {
        this.busy.set(false);
        this.modules.set([...this.modules()]);
        this.notify.error(e);
      }
    });
  }
}
