import { Component, inject, OnInit, signal } from '@angular/core';
import { FormArray, FormBuilder, FormControl, FormGroup, Validators } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { filter, forkJoin, switchMap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { APPROVAL_LINKS, ApprovalDocumentType, ApprovalRequest, ApprovalsApi } from '../../core/approvals.api';
import { Role } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { ApprovalDecisionDialog } from '../../shared/approvals';
import { SHARED } from '../../shared/shared';

/** Approvals inbox: documents waiting for the signed-in user, and the user's own requests. */
@Component({
  selector: 'app-approvals',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'approval.inbox' | t }}</h1></div>
    <mat-tab-group mat-stretch-tabs="false" (selectedIndexChange)="tab.set($event)">
      <mat-tab [label]="('approval.toApprove' | t) + (inbox().length ? ' (' + inbox().length + ')' : '')" />
      <mat-tab [label]="'approval.mine' | t" />
    </mat-tab-group>
    @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
    <div class="cards">
      @for (r of (tab() === 0 ? inbox() : mine()); track r.id) {
        <mat-card appearance="outlined" class="card">
          <div class="row">
            <div class="grow">
              <div class="title">{{ r | bi:'docType' }} · <a class="ltr" [routerLink]="link(r)">{{ r.docNo }}</a></div>
              <div class="secondary">{{ r | bi:'summary' }}</div>
              <div class="secondary">{{ 'approval.requestedBy' | t: { name: r.requestedByName ?? '' } }} · <span class="ltr">{{ r.requestedAt.slice(0, 10) | d }}</span></div>
            </div>
            <div class="amount ltr">{{ r.amount | money }}</div>
          </div>
          <div class="steps">
            @for (s of r.steps; track s.stepNo) {
              <span class="chip" [class.ok]="s.decision === 'Approved'" [class.bad]="s.decision === 'Rejected'"
                [class.warn]="r.status === 'Pending' && s.stepNo === r.currentStep">
                {{ s.stepNo }}. {{ 'role.' + s.roleCode | t }}
              </span>
            }
            @if (tab() === 1) { <span class="chip" [class]="statusChip(r.status)">{{ 'approval.status.' + r.status | t }}</span> }
          </div>
          @if (r.canDecide && r.status === 'Pending') {
            <div class="actions">
              <button mat-stroked-button (click)="decide(r, false)" [disabled]="busy()">{{ 'approval.reject' | t }}</button>
              <button mat-flat-button (click)="decide(r, true)" [disabled]="busy()"><mat-icon>verified</mat-icon> {{ 'approval.approve' | t }}</button>
            </div>
          }
        </mat-card>
      } @empty {
        <p class="empty">{{ (tab() === 0 ? 'approval.nothing' : 'app.none') | t }}</p>
      }
    </div>
  `,
  styles: `
    .cards { display: grid; gap: 12px; margin-top: 16px; max-width: 900px; }
    .card { padding: 16px; }
    .row { display: flex; gap: 16px; align-items: flex-start; }
    .grow { flex: 1; min-width: 0; }
    .title { font-weight: 600; margin-bottom: 4px; }
    .amount { font: var(--mat-sys-title-large); font-weight: 700; color: var(--mat-sys-primary); }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 13px; }
    .steps { display: flex; gap: 6px; flex-wrap: wrap; margin-top: 10px; }
    .actions { display: flex; justify-content: flex-end; gap: 8px; margin-top: 12px; }
    .empty { color: var(--mat-sys-on-surface-variant); padding: 24px 0; }
  `
})
export class ApprovalsPage implements OnInit {
  private readonly api = inject(ApprovalsApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  protected readonly tab = signal(0);
  protected readonly inbox = signal<ApprovalRequest[]>([]);
  protected readonly mine = signal<ApprovalRequest[]>([]);
  protected readonly busy = signal(false);

  ngOnInit(): void {
    this.reload();
  }

  reload(): void {
    this.busy.set(true);
    forkJoin({ inbox: this.api.inbox(), mine: this.api.requests({ mine: true }) }).subscribe({
      next: (r) => {
        this.inbox.set(r.inbox);
        this.mine.set(r.mine);
        this.busy.set(false);
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }

  protected decide(r: ApprovalRequest, approve: boolean): void {
    this.dialog
      .open(ApprovalDecisionDialog, { data: { approve } })
      .afterClosed()
      .pipe(
        filter((c): c is string => c !== undefined),
        switchMap((comment) => (approve ? this.api.approve(r.id, comment || null) : this.api.reject(r.id, comment)))
      )
      .subscribe({
        next: () => {
          this.notify.success(approve ? 'approval.approved' : 'approval.rejected');
          this.reload();
        },
        error: (e) => this.notify.error(e)
      });
  }

  protected link(r: ApprovalRequest): string {
    return APPROVAL_LINKS[r.docType]?.(r.docId) ?? '/approvals';
  }

  protected statusChip(status: string): string {
    return status === 'Approved' ? 'ok' : status === 'Pending' ? 'warn' : status === 'Rejected' ? 'bad' : 'info';
  }
}

type StepGroup = FormGroup<{ minAmount: FormControl<number>; roleCode: FormControl<string> }>;

/** Approval rules per document type: steps by minimum amount and role. */
@Component({
  selector: 'app-approval-rules',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'approval.rules' | t }}</h1></div>
    <p class="secondary">{{ 'approval.rulesHint' | t }}</p>
    <mat-slide-toggle [formControl]="selfApproval" (change)="saveSettings()">{{ 'approval.allowSelf' | t }}</mat-slide-toggle>
    @for (d of types(); track d.docType; let i = $index) {
      <mat-card appearance="outlined" class="card">
        <h3>{{ d | bi }}</h3>
        @for (step of forms[i].controls; track step; let j = $index) {
          <div class="step" [formGroup]="step">
            <span class="no">{{ j + 1 }}</span>
            <mat-form-field>
              <mat-label>{{ 'approval.minAmount' | t }}</mat-label>
              <input matInput type="number" min="0" formControlName="minAmount" dir="ltr" />
            </mat-form-field>
            <mat-form-field>
              <mat-label>{{ 'approval.role' | t }}</mat-label>
              <mat-select formControlName="roleCode">
                @for (r of roles(); track r.code) { <mat-option [value]="r.code">{{ 'role.' + r.code | t }}</mat-option> }
              </mat-select>
            </mat-form-field>
            <button mat-icon-button (click)="forms[i].removeAt(j)" [attr.aria-label]="'app.delete' | t"><mat-icon>delete</mat-icon></button>
          </div>
        } @empty {
          <p class="secondary">{{ 'approval.noSteps' | t }}</p>
        }
        <div class="actions">
          <button mat-button (click)="addStep(i)"><mat-icon>add</mat-icon> {{ 'approval.addStep' | t }}</button>
          <span class="spacer"></span>
          <button mat-flat-button (click)="save(i)" [disabled]="forms[i].invalid">{{ 'app.save' | t }}</button>
        </div>
      </mat-card>
    }
  `,
  styles: `
    .card { padding: 16px; margin-top: 16px; max-width: 760px; }
    h3 { margin: 0 0 12px; }
    .step { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; }
    .no { width: 28px; height: 28px; border-radius: 50%; display: grid; place-items: center; background: var(--mat-sys-secondary-container); }
    .actions { display: flex; align-items: center; gap: 8px; }
    .spacer { flex: 1; }
    .secondary { color: var(--mat-sys-on-surface-variant); }
  `
})
export class ApprovalRulesPage implements OnInit {
  private readonly api = inject(ApprovalsApi);
  private readonly core = inject(ApiService);
  private readonly notify = inject(NotifyService);
  private readonly fb = inject(FormBuilder).nonNullable;
  protected readonly types = signal<ApprovalDocumentType[]>([]);
  protected readonly roles = signal<Role[]>([]);
  protected forms: FormArray<StepGroup>[] = [];
  protected readonly selfApproval = new FormControl(false, { nonNullable: true });

  ngOnInit(): void {
    this.core.roles().subscribe((r) => this.roles.set(r.filter((x) => x.code !== 'employee')));
    this.api.settings().subscribe((s) => this.selfApproval.setValue(s.allowSelfApproval));
    this.api.rules().subscribe({
      next: (types) => {
        this.forms = types.map((t) => new FormArray(t.steps.map((s) => this.step(s.minAmount, s.roleCode))));
        this.types.set(types);
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected addStep(i: number): void {
    const last = this.forms[i].controls.at(-1)?.value.minAmount ?? 0;
    this.forms[i].push(this.step(last, 'general_manager'));
  }

  protected save(i: number): void {
    const steps = this.forms[i].getRawValue().map((s) => ({ minAmount: Number(s.minAmount), roleCode: s.roleCode }));
    this.api.saveRules(this.types()[i].docType, steps).subscribe({
      next: (saved) => {
        this.forms[i] = new FormArray(saved.steps.map((s) => this.step(s.minAmount, s.roleCode)));
        this.types.update((t) => t.map((x, k) => (k === i ? saved : x)));
        this.notify.success();
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected saveSettings(): void {
    this.api.saveSettings(this.selfApproval.value).subscribe({ next: () => this.notify.success(), error: (e) => this.notify.error(e) });
  }

  private step(minAmount: number, roleCode: string): StepGroup {
    return this.fb.group({ minAmount: [minAmount, [Validators.required, Validators.min(0)]], roleCode: [roleCode, Validators.required] });
  }
}
