import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { forkJoin, filter, switchMap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { todayIso } from '../../core/dates';
import { Customer } from '../../core/sales.models';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

interface Lead {
  id: string;
  leadNo: string;
  companyName: string;
  contactName: string | null;
  phone: string | null;
  email: string | null;
  source: string;
  status: 'New' | 'Contacted' | 'Qualified' | 'Lost' | 'Converted';
  customerId: string | null;
  notes: string | null;
}

interface Opportunity {
  id: string;
  opportunityNo: string;
  title: string;
  leadId: string | null;
  leadName: string | null;
  customerId: string | null;
  customerNameEn: string | null;
  customerNameAr: string | null;
  stage: string;
  expectedValue: number;
  probability: number;
  weighted: number;
  expectedCloseDate: string | null;
  quotationNo: string | null;
  notes: string | null;
}

interface Pipeline {
  stages: { stage: string; count: number; value: number; weighted: number }[];
  openValue: number;
  weighted: number;
  wonThisMonth: number;
  wonValueThisMonth: number;
  winRate: number;
}

interface Activity {
  id: string;
  kind: string;
  subject: string;
  dueDate: string;
  done: boolean;
  overdue: boolean;
  regardingEn: string | null;
}

interface CommissionRow {
  userId: string;
  basis: string;
  percent: number;
  revenue: number;
  grossMargin: number;
  collections: number;
  basisAmount: number;
  commission: number;
}

const STAGES = ['Prospecting', 'Qualification', 'Proposal', 'Negotiation', 'Won', 'Lost'];
const SOURCES = ['Referral', 'Website', 'WalkIn', 'Phone', 'Exhibition', 'SocialMedia', 'Other'];
const KINDS = ['Call', 'Meeting', 'Email', 'Whatsapp', 'Visit', 'Task'];

@Component({
  selector: 'app-crm',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'crm.title' | t }}</h1>
      <span class="spacer"></span>
      <button mat-stroked-button (click)="editLead(null)"><mat-icon>person_add</mat-icon> {{ 'crm.newLead' | t }}</button>
      <button mat-flat-button (click)="editOpportunity(null)"><mat-icon>add</mat-icon> {{ 'crm.newOpportunity' | t }}</button>
    </div>
    <mat-tab-group mat-stretch-tabs="false" (selectedIndexChange)="tab($event)">
      <mat-tab [label]="'crm.pipeline' | t">
        @if (pipeline(); as p) {
          <div class="stats">
            <div class="stat"><span>{{ 'crm.open' | t }}</span><b class="ltr">{{ p.openValue | money }}</b></div>
            <div class="stat"><span>{{ 'crm.weighted' | t }}</span><b class="ltr">{{ p.weighted | money }}</b></div>
            <div class="stat"><span>{{ 'crm.wonMonth' | t }}</span><b class="ltr">{{ p.wonThisMonth }} · {{ p.wonValueThisMonth | money }}</b></div>
            <div class="stat"><span>{{ 'crm.winRate' | t }}</span><b class="ltr">{{ p.winRate }}%</b></div>
          </div>
        }
        <div class="board">
          @for (s of stages; track s) {
            <div class="column" [class.closed]="s === 'Won' || s === 'Lost'">
              <div class="col-head">{{ 'crm.stage.' + s | t }} <span class="secondary ltr">{{ stageTotal(s) | money }}</span></div>
              @for (o of byStage(s); track o.id) {
                <div class="card" (click)="editOpportunity(o)">
                  <div class="title">{{ o.title }}</div>
                  <div class="secondary">{{ o.customerNameEn ? (o | bi:'customerName') : o.leadName }}</div>
                  <div class="row"><b class="ltr">{{ o.expectedValue | money }}</b><span class="secondary ltr">{{ o.probability }}%</span></div>
                  @if (o.expectedCloseDate) { <div class="secondary ltr">{{ o.expectedCloseDate | d }}</div> }
                </div>
              }
            </div>
          }
        </div>
      </mat-tab>
      <mat-tab [label]="'crm.leads' | t">
        <table class="line-editor tab-body">
          <thead><tr><th>{{ 'crm.leadNo' | t }}</th><th>{{ 'crm.company' | t }}</th><th>{{ 'crm.contact' | t }}</th><th>{{ 'crm.source' | t }}</th>
            <th>{{ 'app.status' | t }}</th><th></th></tr></thead>
          <tbody>
            @for (l of leads(); track l.id) {
              <tr>
                <td class="ltr">{{ l.leadNo }}</td><td>{{ l.companyName }}</td>
                <td>{{ l.contactName }} <span class="secondary ltr">{{ l.phone }}</span></td>
                <td>{{ 'crm.sourceName.' + l.source | t }}</td>
                <td><span class="chip" [class.ok]="l.status === 'Converted'" [class.bad]="l.status === 'Lost'" [class.warn]="l.status === 'New'">{{ 'crm.leadStatus.' + l.status | t }}</span></td>
                <td class="actions-cell">
                  @if (l.status !== 'Converted' && l.status !== 'Lost') {
                    <button mat-icon-button (click)="editLead(l)"><mat-icon>edit</mat-icon></button>
                    <button mat-icon-button (click)="addActivity({ leadId: l.id })" [matTooltip]="'crm.newActivity' | t"><mat-icon>event</mat-icon></button>
                    @if (auth.can('sal.customers:manage')) { <button mat-stroked-button (click)="convert(l)">{{ 'crm.convert' | t }}</button> }
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </mat-tab>
      <mat-tab [label]="'crm.activities' | t">
        <div class="tab-body">
          @for (a of activities(); track a.id) {
            <div class="activity" [class.overdue]="a.overdue">
              <mat-icon>{{ icon(a.kind) }}</mat-icon>
              <div class="grow"><b>{{ a.subject }}</b><div class="secondary">{{ a.regardingEn }} · <span class="ltr">{{ a.dueDate | d }}</span></div></div>
              @if (a.overdue) { <span class="chip bad">{{ 'crm.overdue' | t }}</span> }
              <button mat-stroked-button (click)="done(a)"><mat-icon>check</mat-icon> {{ 'crm.done' | t }}</button>
            </div>
          } @empty {
            <p class="secondary">{{ 'crm.noActivities' | t }}</p>
          }
        </div>
      </mat-tab>
      @if (auth.can('sal.commissions:manage')) {
        <mat-tab [label]="'crm.commissions' | t">
          <div class="toolbar-row tab-body">
            <mat-form-field><mat-label>{{ 'payroll.month' | t }}</mat-label><input matInput type="month" [formControl]="month" /></mat-form-field>
            <button mat-flat-button (click)="loadCommissions()">{{ 'finance.run' | t }}</button>
          </div>
          <table class="line-editor">
            <thead><tr><th>{{ 'crm.salesperson' | t }}</th><th>{{ 'crm.basis' | t }}</th><th class="num">%</th><th class="num">{{ 'crm.revenue' | t }}</th>
              <th class="num">{{ 'crm.margin' | t }}</th><th class="num">{{ 'crm.collections' | t }}</th><th class="num">{{ 'crm.commission' | t }}</th><th></th></tr></thead>
            <tbody>
              @for (r of commissions(); track r.userId) {
                <tr>
                  <td>{{ userName(r.userId) }}</td><td>{{ 'crm.basisName.' + r.basis | t }}</td><td class="num ltr">{{ r.percent }}</td>
                  <td class="num ltr">{{ r.revenue | money }}</td><td class="num ltr">{{ r.grossMargin | money }}</td><td class="num ltr">{{ r.collections | money }}</td>
                  <td class="num ltr"><b>{{ r.commission | money }}</b></td>
                  <td><button mat-icon-button (click)="editRate(r)"><mat-icon>edit</mat-icon></button></td>
                </tr>
              }
            </tbody>
          </table>
          <p class="secondary">{{ 'crm.commissionHint' | t }}</p>
        </mat-tab>
      }
    </mat-tab-group>
  `,
  styles: `
    .spacer { flex: 1; }
    .tab-body { margin-block-start: 16px; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 12px; }
    .stats { display: flex; gap: 12px; flex-wrap: wrap; margin: 16px 0; }
    .stat { display: flex; flex-direction: column; padding: 10px 16px; border-radius: 12px; background: var(--mat-sys-surface-container); min-width: 160px; }
    .stat b { font-size: 18px; color: var(--mat-sys-primary); }
    .board { display: grid; grid-template-columns: repeat(6, minmax(190px, 1fr)); gap: 10px; overflow-x: auto; padding-bottom: 8px; }
    .column { background: var(--mat-sys-surface-container-low); border-radius: 12px; padding: 8px; min-height: 200px; }
    .column.closed { opacity: 0.85; }
    .col-head { font-weight: 600; padding: 4px 6px 8px; display: flex; justify-content: space-between; gap: 6px; }
    .card { background: var(--mat-sys-surface); border: 1px solid var(--mat-sys-outline-variant); border-radius: 10px; padding: 8px 10px; margin-bottom: 8px; cursor: pointer; }
    .card .title { font-weight: 600; }
    .card .row { display: flex; justify-content: space-between; margin-top: 4px; }
    .activity { display: flex; align-items: center; gap: 12px; padding: 10px; border-bottom: 1px solid var(--mat-sys-outline-variant); }
    .activity.overdue mat-icon { color: #b3261e; }
    .grow { flex: 1; }
    .actions-cell { white-space: nowrap; text-align: end; }
  `
})
export class CrmPage implements OnInit {
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  protected readonly stages = STAGES;
  protected readonly pipeline = signal<Pipeline | null>(null);
  protected readonly opportunities = signal<Opportunity[]>([]);
  protected readonly leads = signal<Lead[]>([]);
  protected readonly activities = signal<Activity[]>([]);
  protected readonly commissions = signal<CommissionRow[]>([]);
  protected readonly customers = signal<Customer[]>([]);
  private readonly users = signal<{ id: string; displayNameEn: string }[]>([]);
  protected readonly month = new FormControl(todayIso().slice(0, 7), { nonNullable: true });

  ngOnInit(): void {
    this.api.get<Customer[]>('sales/customers').subscribe((c) => this.customers.set(c));
    this.load();
  }

  private load(): void {
    forkJoin({
      pipeline: this.api.get<Pipeline>('sales/crm/pipeline'),
      opportunities: this.api.get<Opportunity[]>('sales/crm/opportunities'),
      leads: this.api.get<Lead[]>('sales/crm/leads'),
      activities: this.api.get<Activity[]>('sales/crm/activities')
    }).subscribe({
      next: (r) => {
        this.pipeline.set(r.pipeline);
        this.opportunities.set(r.opportunities);
        this.leads.set(r.leads);
        this.activities.set(r.activities);
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected tab(index: number): void {
    if (index === 3 && !this.commissions().length) {
      this.api.users().subscribe({ next: (u) => this.users.set(u), error: () => undefined });
      this.loadCommissions();
    }
  }

  protected byStage(stage: string): Opportunity[] {
    return this.opportunities().filter((o) => o.stage === stage);
  }

  protected stageTotal(stage: string): number {
    return this.byStage(stage).reduce((s, o) => s + o.expectedValue, 0);
  }

  protected icon(kind: string): string {
    return { Call: 'call', Meeting: 'groups', Email: 'mail', Whatsapp: 'chat', Visit: 'directions_car', Task: 'task_alt' }[kind] ?? 'event';
  }

  protected userName(id: string): string {
    return this.users().find((u) => u.id === id)?.displayNameEn ?? id.slice(0, 8);
  }

  protected editLead(l: Lead | null): void {
    this.dialog.open(LeadDialog, { data: l, width: '560px' }).afterClosed().pipe(filter(Boolean)).subscribe(() => this.load());
  }

  protected editOpportunity(o: Opportunity | null): void {
    this.dialog.open(OpportunityDialog, { data: { opportunity: o, customers: this.customers(), leads: this.leads().filter((l) => l.status !== 'Lost') }, width: '600px' })
      .afterClosed().pipe(filter(Boolean)).subscribe(() => this.load());
  }

  protected addActivity(regarding: { leadId?: string; opportunityId?: string; customerId?: string }): void {
    this.dialog.open(ActivityDialog, { data: regarding, width: '480px' }).afterClosed().pipe(filter(Boolean)).subscribe(() => this.load());
  }

  protected done(a: Activity): void {
    this.api.post(`sales/crm/activities/${a.id}/done`).subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  protected convert(l: Lead): void {
    const code = prompt('Customer code / رمز العميل', l.companyName.replace(/[^A-Za-z0-9]/g, '').slice(0, 10).toUpperCase());
    if (!code) return;
    this.api.post(`sales/crm/leads/${l.id}/convert`, { customerCode: code, opportunityTitle: null, expectedValue: null }).subscribe({
      next: () => {
        this.notify.success('crm.converted');
        this.load();
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected loadCommissions(): void {
    const [year, month] = this.month.value.split('-').map(Number);
    this.api.get<CommissionRow[]>('sales/commissions/report', { year, month }).subscribe({ next: (r) => this.commissions.set(r), error: (e) => this.notify.error(e) });
  }

  protected editRate(r: CommissionRow): void {
    const percent = prompt('Commission % / نسبة العمولة', String(r.percent));
    if (percent === null) return;
    const basis = prompt('Basis: Revenue, GrossMargin or Collections', r.basis) ?? r.basis;
    this.api.put('sales/commissions/rates', { userId: r.userId, basis, percent: Number(percent) }).subscribe({
      next: () => this.loadCommissions(),
      error: (e) => this.notify.error(e)
    });
  }
}

@Component({
  selector: 'app-lead-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ (data ? 'app.edit' : 'crm.newLead') | t }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field class="wide"><mat-label>{{ 'crm.company' | t }}</mat-label><input matInput formControlName="companyName" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'crm.contact' | t }}</mat-label><input matInput formControlName="contactName" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'employee.mobile' | t }}</mat-label><input matInput formControlName="phone" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'employee.email' | t }}</mat-label><input matInput formControlName="email" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'crm.source' | t }}</mat-label>
          <mat-select formControlName="source">@for (s of sources; track s) { <mat-option [value]="s">{{ 'crm.sourceName.' + s | t }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>{{ 'app.status' | t }}</mat-label>
          <mat-select formControlName="status">@for (s of statuses; track s) { <mat-option [value]="s">{{ 'crm.leadStatus.' + s | t }}</mat-option> }</mat-select></mat-form-field>
        @if (form.value.status === 'Lost') {
          <mat-form-field class="wide"><mat-label>{{ 'crm.lostReason' | t }}</mat-label><input matInput formControlName="lostReason" /></mat-form-field>
        }
        <mat-form-field class="wide"><mat-label>{{ 'app.notes' | t }}</mat-label><textarea matInput formControlName="notes" rows="2"></textarea></mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid" (click)="save()">{{ 'app.save' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `.wide { grid-column: 1 / -1; }`
})
export class LeadDialog {
  protected readonly data = inject<Lead | null>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<LeadDialog, boolean>);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly sources = SOURCES;
  protected readonly statuses = ['New', 'Contacted', 'Qualified', 'Lost'];
  protected readonly form = inject(FormBuilder).nonNullable.group({
    companyName: [this.data?.companyName ?? '', Validators.required], contactName: [this.data?.contactName ?? ''], phone: [this.data?.phone ?? ''],
    email: [this.data?.email ?? ''], source: [this.data?.source ?? 'Referral'], status: [this.data?.status ?? 'New'], lostReason: [''],
    notes: [this.data?.notes ?? '']
  });

  protected save(): void {
    const v = this.form.getRawValue();
    const body = { ...v, lostReason: v.status === 'Lost' ? v.lostReason : null };
    (this.data ? this.api.put(`sales/crm/leads/${this.data.id}`, body) : this.api.post('sales/crm/leads', body))
      .subscribe({ next: () => this.ref.close(true), error: (e) => this.notify.error(e) });
  }
}

@Component({
  selector: 'app-opportunity-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ (data.opportunity ? 'app.edit' : 'crm.newOpportunity') | t }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field class="wide"><mat-label>{{ 'crm.oppTitle' | t }}</mat-label><input matInput formControlName="title" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'cust.customer' | t }}</mat-label>
          <mat-select formControlName="customerId"><mat-option value="">—</mat-option>
            @for (c of data.customers; track c.id) { <mat-option [value]="c.id">{{ c | bi }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>{{ 'crm.lead' | t }}</mat-label>
          <mat-select formControlName="leadId"><mat-option value="">—</mat-option>
            @for (l of data.leads; track l.id) { <mat-option [value]="l.id">{{ l.companyName }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>{{ 'crm.stageLabel' | t }}</mat-label>
          <mat-select formControlName="stage">@for (s of stages; track s) { <mat-option [value]="s">{{ 'crm.stage.' + s | t }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>{{ 'crm.value' | t }}</mat-label><input matInput type="number" min="0" formControlName="expectedValue" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'crm.probability' | t }}</mat-label><input matInput type="number" min="0" max="100" formControlName="probability" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'crm.closeDate' | t }}</mat-label><input matInput type="date" formControlName="expectedCloseDate" /></mat-form-field>
        @if (form.value.stage === 'Lost') {
          <mat-form-field class="wide"><mat-label>{{ 'crm.lostReason' | t }}</mat-label><input matInput formControlName="lostReason" /></mat-form-field>
        }
        <mat-form-field class="wide"><mat-label>{{ 'app.notes' | t }}</mat-label><textarea matInput formControlName="notes" rows="2"></textarea></mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid || (!form.value.customerId && !form.value.leadId)" (click)="save()">{{ 'app.save' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `.wide { grid-column: 1 / -1; }`
})
export class OpportunityDialog {
  protected readonly data = inject<{ opportunity: Opportunity | null; customers: Customer[]; leads: Lead[] }>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<OpportunityDialog, boolean>);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly stages = STAGES;
  private readonly o = this.data.opportunity;
  protected readonly form = inject(FormBuilder).nonNullable.group({
    title: [this.o?.title ?? '', Validators.required], customerId: [this.o?.customerId ?? ''], leadId: [this.o?.leadId ?? ''],
    stage: [this.o?.stage ?? 'Prospecting'], expectedValue: [this.o?.expectedValue ?? 0], probability: [this.o?.probability ?? (null as number | null)],
    expectedCloseDate: [this.o?.expectedCloseDate ?? ''], lostReason: [''], notes: [this.o?.notes ?? '']
  });

  protected save(): void {
    const v = this.form.getRawValue();
    const body = {
      ...v, customerId: v.customerId || null, leadId: v.leadId || null, expectedValue: Number(v.expectedValue) || 0,
      probability: v.probability === null || (v.probability as unknown) === '' ? null : Number(v.probability), expectedCloseDate: v.expectedCloseDate || null,
      lostReason: v.stage === 'Lost' ? v.lostReason : null
    };
    (this.o ? this.api.put(`sales/crm/opportunities/${this.o.id}`, body) : this.api.post('sales/crm/opportunities', body))
      .subscribe({ next: () => this.ref.close(true), error: (e) => this.notify.error(e) });
  }
}

@Component({
  selector: 'app-activity-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'crm.newActivity' | t }}</h2>
    <mat-dialog-content>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field><mat-label>{{ 'crm.kind' | t }}</mat-label>
          <mat-select formControlName="kind">@for (k of kinds; track k) { <mat-option [value]="k">{{ 'crm.kindName.' + k | t }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>{{ 'crm.due' | t }}</mat-label><input matInput type="date" formControlName="dueDate" /></mat-form-field>
        <mat-form-field class="wide"><mat-label>{{ 'crm.subject' | t }}</mat-label><input matInput formControlName="subject" /></mat-form-field>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid" (click)="save()">{{ 'app.save' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `.wide { grid-column: 1 / -1; }`
})
export class ActivityDialog {
  protected readonly data = inject<{ leadId?: string; opportunityId?: string; customerId?: string }>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<ActivityDialog, boolean>);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly kinds = KINDS;
  protected readonly form = inject(FormBuilder).nonNullable.group({ kind: ['Call'], dueDate: [todayIso(), Validators.required], subject: ['', Validators.required] });

  protected save(): void {
    this.api.post('sales/crm/activities', { ...this.data, ...this.form.getRawValue() }).subscribe({ next: () => this.ref.close(true), error: (e) => this.notify.error(e) });
  }
}
