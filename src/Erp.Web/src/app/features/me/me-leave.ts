import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { filter, switchMap } from 'rxjs';
import { daysBetween, todayIso } from '../../core/dates';
import { MeApi, MyLeave, MyLeaveRequest } from '../../core/me.api';
import { NotifyService } from '../../core/notify.service';
import { ConfirmDialog } from '../../shared/dialogs';
import { SHARED } from '../../shared/shared';

@Component({
  selector: 'app-me-leave',
  imports: [...SHARED],
  template: `
    @if (leave(); as l) {
      <div class="card hero">
        <div class="muted">{{ 'me.leaveDaysLeft' | t }}</div>
        <div class="big ltr">{{ l.balance.balance }}</div>
        <div class="muted">
          {{ 'me.leaveAccrued' | t: { accrued: l.balance.accrued, taken: l.balance.taken, perYear: l.balance.entitlementDaysPerYear } }}
        </div>
      </div>

      @if (!applying()) {
        <button mat-flat-button class="full" (click)="applying.set(true)"><mat-icon>add</mat-icon> {{ 'me.applyLeave' | t }}</button>
      } @else {
        <form class="card" [formGroup]="form" (ngSubmit)="submit()">
          <h3>{{ 'me.applyLeave' | t }}</h3>
          <mat-form-field class="full">
            <mat-label>{{ 'leave.type' | t }}</mat-label>
            <mat-select formControlName="leaveTypeCode">
              @for (t of l.types; track t.code) { <mat-option [value]="t.code">{{ t | bi }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <div class="two">
            <mat-form-field>
              <mat-label>{{ 'app.from' | t }}</mat-label>
              <input matInput type="date" formControlName="fromDate" dir="ltr" />
            </mat-form-field>
            <mat-form-field>
              <mat-label>{{ 'app.to' | t }}</mat-label>
              <input matInput type="date" formControlName="toDate" dir="ltr" />
            </mat-form-field>
          </div>
          @if (days() > 0) { <p class="muted">{{ 'me.leaveDays' | t: { days: days() } }}</p> }
          <mat-form-field class="full">
            <mat-label>{{ 'app.reason' | t }}</mat-label>
            <textarea matInput formControlName="reason" rows="2"></textarea>
          </mat-form-field>
          <div class="actions">
            <button mat-button type="button" (click)="applying.set(false)">{{ 'app.cancel' | t }}</button>
            <button mat-flat-button type="submit" [disabled]="form.invalid || days() <= 0 || busy()">{{ 'me.submitRequest' | t }}</button>
          </div>
        </form>
      }

      <h2>{{ 'me.myRequests' | t }}</h2>
      <div class="card">
        @for (r of l.requests; track r.id) {
          <div class="list-item">
            <div class="grow">
              <div>{{ r | bi:'leaveType' }} · <span class="ltr">{{ r.days }}</span> {{ 'me.days' | t }}</div>
              <div class="muted"><span class="ltr">{{ r.fromDate | d }}</span> → <span class="ltr">{{ r.toDate | d }}</span></div>
            </div>
            <span class="chip" [class.ok]="r.status === 'Approved'" [class.warn]="r.status === 'Pending'" [class.bad]="r.status === 'Rejected'">
              {{ 'leave.status.' + r.status | t }}
            </span>
            @if (r.status === 'Pending') {
              <button mat-icon-button (click)="cancel(r)" [attr.aria-label]="'me.cancelRequest' | t"><mat-icon>close</mat-icon></button>
            }
          </div>
        } @empty {
          <div class="empty">{{ 'me.noRequests' | t }}</div>
        }
      </div>
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: `
    h2 { font: var(--mat-sys-title-medium); margin: 20px 0 8px; }
    h3 { font: var(--mat-sys-title-small); margin: 0 0 12px; }
    .card { display: block; background: var(--mat-sys-surface); border-radius: 16px; padding: 16px; margin-bottom: 12px;
      border: 1px solid var(--mat-sys-outline-variant); }
    .hero { background: color-mix(in srgb, var(--mat-sys-primary) 10%, var(--mat-sys-surface)); border: 0; }
    .big { font: var(--mat-sys-headline-small); font-weight: 700; color: var(--mat-sys-primary); }
    .muted { color: var(--mat-sys-on-surface-variant); font-size: 13px; }
    .full { width: 100%; }
    button.full { height: 48px; margin-bottom: 4px; }
    .two { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
    .actions { display: flex; justify-content: flex-end; gap: 8px; }
    .list-item { display: flex; align-items: center; gap: 8px; padding: 12px 0; border-bottom: 1px solid var(--mat-sys-outline-variant); }
    .list-item:last-child { border-bottom: 0; }
    .grow { flex: 1; min-width: 0; }
    .empty { text-align: center; color: var(--mat-sys-on-surface-variant); padding: 24px 0; }
  `
})
export class MeLeavePage implements OnInit {
  /** `?apply=1` opens the form straight away (from the home screen). */
  readonly apply = input<string>();
  private readonly me = inject(MeApi);
  private readonly notify = inject(NotifyService);
  private readonly dialog = inject(MatDialog);
  protected readonly leave = signal<MyLeave | null>(null);
  protected readonly applying = signal(false);
  protected readonly busy = signal(false);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    leaveTypeCode: ['ANNUAL', Validators.required],
    fromDate: [todayIso(), Validators.required],
    toDate: [todayIso(), Validators.required],
    reason: ['']
  });
  private readonly range = signal({ from: todayIso(), to: todayIso() });
  protected readonly days = computed(() => {
    const { from, to } = this.range();
    return from && to ? daysBetween(from, to) + 1 : 0;
  });

  ngOnInit(): void {
    if (this.apply()) this.applying.set(true);
    this.form.valueChanges.subscribe((v) => this.range.set({ from: v.fromDate ?? '', to: v.toDate ?? '' }));
    this.reload();
  }

  reload(): void {
    this.me.leave().subscribe({ next: (l) => this.leave.set(l), error: (e) => this.notify.error(e) });
  }

  submit(): void {
    if (this.form.invalid) return;
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.me.applyLeave({ ...v, reason: v.reason.trim() || null }).subscribe({
      next: () => {
        this.busy.set(false);
        this.applying.set(false);
        this.notify.success('me.requestSent');
        this.reload();
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }

  cancel(r: MyLeaveRequest): void {
    this.dialog
      .open(ConfirmDialog, { data: { title: 'me.cancelRequest', message: 'me.cancelConfirm' } })
      .afterClosed()
      .pipe(filter(Boolean), switchMap(() => this.me.cancelLeave(r.id)))
      .subscribe({ next: () => this.reload(), error: (e) => this.notify.error(e) });
  }
}
