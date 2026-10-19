import { Component, effect, inject, input, signal } from '@angular/core';
import { FormControl, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { ApprovalRequest, ApprovalsApi } from '../core/approvals.api';
import { SHARED } from './shared';

/** Asks for an optional comment (approve) or a required reason (reject). Closes with the text, or undefined when cancelled. */
@Component({
  selector: 'app-approval-decision-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ (data.approve ? 'approval.approveTitle' : 'approval.rejectTitle') | t }}</h2>
    <mat-dialog-content>
      <mat-form-field class="full">
        <mat-label>{{ (data.approve ? 'approval.comment' : 'approval.reason') | t }}</mat-label>
        <textarea matInput [formControl]="comment" rows="3"></textarea>
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="!data.approve && !comment.value.trim()" (click)="ref.close(comment.value.trim())">
        {{ (data.approve ? 'approval.approve' : 'approval.reject') | t }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `.full { width: 100%; min-width: min(420px, 80vw); }`
})
export class ApprovalDecisionDialog {
  protected readonly data = inject<{ approve: boolean }>(MAT_DIALOG_DATA);
  protected readonly ref = inject(MatDialogRef<ApprovalDecisionDialog, string>);
  protected readonly comment = new FormControl('', { nonNullable: true, validators: [Validators.maxLength(500)] });
}

/** Step-by-step approval history of one document (latest request first). */
@Component({
  selector: 'app-approval-trail',
  imports: [...SHARED],
  template: `
    @for (r of requests(); track r.id) {
      <div class="trail">
        <div class="head"><mat-icon>approval</mat-icon> {{ 'approval.title' | t }} · <span class="chip" [class]="chip(r.status)">{{ 'approval.status.' + r.status | t }}</span>
          <span class="secondary">{{ r.requestedByName }} · <span class="ltr">{{ r.requestedAt.slice(0, 10) | d }}</span></span></div>
        <ol>
          @for (s of r.steps; track s.stepNo) {
            <li [class.current]="r.status === 'Pending' && s.stepNo === r.currentStep">
              <span class="role">{{ 'role.' + s.roleCode | t }}</span>
              @if (s.decision) {
                <span class="chip" [class.ok]="s.decision === 'Approved'" [class.bad]="s.decision === 'Rejected'">{{ 'approval.decision.' + s.decision | t }}</span>
                <span class="secondary">{{ s.decidedByName }}
                  @if (s.onBehalfOfName) { ({{ 'approval.onBehalfOf' | t: { name: s.onBehalfOfName } }}) }
                  · <span class="ltr">{{ s.decidedAt!.slice(0, 10) | d }}</span></span>
                @if (s.comment) { <div class="comment">“{{ s.comment }}”</div> }
              } @else if (r.status === 'Pending' && s.stepNo === r.currentStep) {
                <span class="chip warn">{{ 'approval.waiting' | t }}</span>
              }
            </li>
          }
        </ol>
      </div>
    }
  `,
  styles: `
    .trail { border: 1px solid var(--mat-sys-outline-variant); border-radius: 12px; padding: 12px 16px; margin: 12px 0; }
    .head { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; font-weight: 600; }
    .secondary { font-weight: 400; color: var(--mat-sys-on-surface-variant); font-size: 13px; }
    ol { margin: 8px 0 0; padding-inline-start: 20px; }
    li { padding: 4px 0; }
    li.current { font-weight: 600; }
    .role { margin-inline-end: 8px; }
    .comment { color: var(--mat-sys-on-surface-variant); font-style: italic; }
  `
})
export class ApprovalTrail {
  readonly docType = input.required<string>();
  readonly docId = input.required<string>();
  /** Bump to reload after an action. */
  readonly version = input(0);
  private readonly api = inject(ApprovalsApi);
  protected readonly requests = signal<ApprovalRequest[]>([]);

  constructor() {
    effect(() => {
      this.version();
      this.api.requests({ docType: this.docType(), docId: this.docId() }).subscribe({ next: (r) => this.requests.set(r), error: () => undefined });
    });
  }

  protected chip(status: string): string {
    return status === 'Approved' ? 'ok' : status === 'Pending' ? 'warn' : status === 'Rejected' ? 'bad' : 'info';
  }
}
