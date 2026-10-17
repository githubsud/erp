import { Component, inject, input, OnInit, output, signal } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { filter, switchMap } from 'rxjs';
import { ApiService } from '../../../core/api.service';
import { AuthService } from '../../../core/auth.service';
import { todayIso } from '../../../core/dates';
import { Employee, Settlement, SettlementReason } from '../../../core/models';
import { NotifyService } from '../../../core/notify.service';
import { ConfirmDialog, PaymentDialog, PaymentResult, ReverseDialog, ReverseResult } from '../../../shared/dialogs';
import { SHARED } from '../../../shared/shared';

@Component({
  selector: 'app-settlement-tab',
  imports: [...SHARED],
  template: `
    @if (settlement(); as s) {
      <div class="head">
        <div>
          <div class="section-title">{{ s.settlementNo }}</div>
          <span class="chip" [class.warn]="s.status === 'Draft'" [class.ok]="s.status === 'Paid'" [class.info]="s.status === 'Posted'">
            {{ 'settlement.status.' + s.status | t }}
          </span>
        </div>
        <div class="actions">
          <button mat-stroked-button (click)="pdf(s)"><mat-icon>picture_as_pdf</mat-icon> {{ 'settlement.pdf' | t }}</button>
          @if (s.status === 'Draft' && auth.can('hr.payroll:prepare')) {
            <button mat-button (click)="discard(s)"><mat-icon>delete</mat-icon> {{ 'settlement.discard' | t }}</button>
          }
          @if (s.status === 'Draft' && auth.can('hr.payroll:post')) {
            <button mat-flat-button (click)="post(s)"><mat-icon>task_alt</mat-icon> {{ 'settlement.post' | t }}</button>
          }
          @if (s.status === 'Posted' && auth.can('hr.payroll:post')) {
            <button mat-flat-button (click)="pay(s)"><mat-icon>payments</mat-icon> {{ 'payroll.markPaid' | t }}</button>
            <button mat-button (click)="reverse(s)"><mat-icon>undo</mat-icon> {{ 'payroll.reverse' | t }}</button>
          }
        </div>
      </div>

      <div class="card-grid">
        <mat-card appearance="outlined" class="stat">
          <div class="label">{{ 'settlement.service' | t }}</div>
          <div class="value">{{ s.serviceDays }} <small>{{ 'app.days' | t }}</small></div>
          <div class="hint ltr">{{ s.joiningDate | d }} → {{ s.lastWorkingDay | d }}</div>
        </mat-card>
        <mat-card appearance="outlined" class="stat">
          <div class="label">{{ 'settlement.gratuity' | t }}</div>
          <div class="value ltr">{{ s.gratuityAmount | money }}</div>
          @if (!s.eligibleForGratuity) { <div class="hint">{{ 'settlement.notEligible' | t }}</div> }
        </mat-card>
        <mat-card appearance="outlined" class="stat">
          <div class="label">{{ 'settlement.leaveBalance' | t }}</div>
          <div class="value ltr">{{ s.leaveBalanceDays }}</div>
        </mat-card>
        <mat-card appearance="outlined" class="stat net">
          <div class="label">{{ 'settlement.net' | t }}</div>
          <div class="value ltr">{{ s.netPayable | money }}</div>
          <div class="hint">{{ 'app.qar' | t }}</div>
        </mat-card>
      </div>

      <div class="table-wrap">
        <table mat-table [dataSource]="s.lines">
          <ng-container matColumnDef="name">
            <th mat-header-cell *matHeaderCellDef>{{ 'payroll.lines' | t }}</th>
            <td mat-cell *matCellDef="let l">{{ l | bi }}</td>
          </ng-container>
          <ng-container matColumnDef="qty">
            <th mat-header-cell *matHeaderCellDef></th>
            <td mat-cell *matCellDef="let l" class="ltr">{{ l.quantity }}</td>
          </ng-container>
          <ng-container matColumnDef="earning">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'settlement.earnings' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr">{{ l.kind === 'Earning' ? (l.amount | money) : '' }}</td>
          </ng-container>
          <ng-container matColumnDef="deduction">
            <th mat-header-cell *matHeaderCellDef class="num">{{ 'settlement.deductions' | t }}</th>
            <td mat-cell *matCellDef="let l" class="num ltr">{{ l.kind === 'Deduction' ? (l.amount | money) : '' }}</td>
          </ng-container>
          <tr mat-header-row *matHeaderRowDef="columns"></tr>
          <tr mat-row *matRowDef="let row; columns: columns"></tr>
        </table>
      </div>
    } @else if (loaded() && auth.can('hr.payroll:prepare') && employee().status === 'Active') {
      <mat-card appearance="outlined" class="form-card">
        <div class="section-title">{{ 'settlement.new' | t }}</div>
        <form [formGroup]="form" (ngSubmit)="prepare()">
          <div class="form-grid">
            <mat-form-field>
              <mat-label>{{ 'settlement.lastDay' | t }}</mat-label>
              <input matInput type="date" formControlName="lastWorkingDay" />
            </mat-form-field>
            <mat-form-field>
              <mat-label>{{ 'settlement.reason' | t }}</mat-label>
              <mat-select formControlName="reason">
                @for (r of reasons; track r) { <mat-option [value]="r">{{ 'settlement.reason.' + r | t }}</mat-option> }
              </mat-select>
            </mat-form-field>
            <mat-form-field>
              <mat-label>{{ 'settlement.noticePay' | t }}</mat-label>
              <input matInput type="number" min="0" formControlName="noticePay" dir="ltr" />
            </mat-form-field>
            <mat-form-field>
              <mat-label>{{ 'settlement.airTicket' | t }}</mat-label>
              <input matInput type="number" min="0" formControlName="airTicket" dir="ltr" />
            </mat-form-field>
            <mat-form-field>
              <mat-label>{{ 'settlement.otherEarnings' | t }}</mat-label>
              <input matInput type="number" min="0" formControlName="otherEarnings" dir="ltr" />
            </mat-form-field>
            <mat-form-field>
              <mat-label>{{ 'settlement.otherDeductions' | t }}</mat-label>
              <input matInput type="number" min="0" formControlName="otherDeductions" dir="ltr" />
            </mat-form-field>
            <mat-form-field>
              <mat-label>{{ 'settlement.advanceRecovery' | t }}</mat-label>
              <input matInput type="number" min="0" formControlName="advanceRecovery" dir="ltr" />
            </mat-form-field>
            <mat-form-field class="full">
              <mat-label>{{ 'app.notes' | t }}</mat-label>
              <input matInput formControlName="notes" />
            </mat-form-field>
          </div>
          <div class="actions"><button mat-flat-button [disabled]="form.invalid || busy()"><mat-icon>calculate</mat-icon> {{ 'payroll.calculate' | t }}</button></div>
        </form>
      </mat-card>
    } @else if (loaded()) {
      <div class="empty">{{ 'app.none' | t }}</div>
    }
  `,
  styles: `
    .head { display: flex; flex-wrap: wrap; justify-content: space-between; gap: 12px; align-items: center; margin-bottom: 16px; }
    .net .value { color: var(--mat-sys-primary); }
    .form-card { padding: 4px 20px 20px; }
  `
})
export class SettlementTab implements OnInit {
  readonly employee = input.required<Employee>();
  readonly changed = output<void>();
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  private readonly dialog = inject(MatDialog);

  protected readonly reasons: SettlementReason[] = ['Resignation', 'Termination', 'EndOfContract', 'Retirement', 'Death'];
  protected readonly columns = ['name', 'qty', 'earning', 'deduction'];
  protected readonly settlement = signal<Settlement | null>(null);
  protected readonly loaded = signal(false);
  protected readonly busy = signal(false);
  protected readonly form = inject(FormBuilder).group({
    lastWorkingDay: [todayIso(), Validators.required],
    reason: ['Resignation' as SettlementReason, Validators.required],
    noticePay: [0],
    airTicket: [0],
    otherEarnings: [0],
    otherDeductions: [0],
    advanceRecovery: [null as number | null],
    notes: ['']
  });

  ngOnInit(): void {
    this.load();
  }

  protected prepare(): void {
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.api
      .createSettlement(this.employee().id, {
        ...v,
        noticePay: Number(v.noticePay ?? 0),
        airTicket: Number(v.airTicket ?? 0),
        otherEarnings: Number(v.otherEarnings ?? 0),
        otherDeductions: Number(v.otherDeductions ?? 0),
        advanceRecovery: v.advanceRecovery === null || (v.advanceRecovery as unknown) === '' ? null : Number(v.advanceRecovery),
        notes: v.notes || null
      })
      .subscribe({
        next: (s) => {
          this.busy.set(false);
          this.settlement.set(s);
        },
        error: (e) => {
          this.busy.set(false);
          this.notify.error(e);
        }
      });
  }

  protected pdf(s: Settlement): void {
    this.api.download(`hr/final-settlements/${s.id}/pdf`).subscribe({ error: (e) => this.notify.error(e) });
  }

  protected discard(s: Settlement): void {
    this.api.deleteSettlement(s.id).subscribe({ next: () => this.settlement.set(null), error: (e) => this.notify.error(e) });
  }

  protected post(s: Settlement): void {
    this.dialog
      .open(ConfirmDialog, { data: { title: 'settlement.post', message: 'settlement.postWarning' } })
      .afterClosed()
      .pipe(filter(Boolean), switchMap(() => this.api.settlementAction(s.id, 'post')))
      .subscribe({ next: (r) => this.done(r), error: (e) => this.notify.error(e) });
  }

  protected pay(s: Settlement): void {
    this.dialog
      .open(PaymentDialog, { data: { title: 'payroll.markPaid', date: todayIso() } })
      .afterClosed()
      .pipe(filter((r): r is PaymentResult => !!r), switchMap((r) => this.api.markSettlementPaid(s.id, r.paymentAccountId, r.paymentDate)))
      .subscribe({ next: (r) => this.done(r), error: (e) => this.notify.error(e) });
  }

  protected reverse(s: Settlement): void {
    this.dialog
      .open(ReverseDialog, { data: { date: s.lastWorkingDay } })
      .afterClosed()
      .pipe(filter((r): r is ReverseResult => !!r), switchMap((r) => this.api.reverseSettlement(s.id, r.date, r.reason)))
      .subscribe({
        next: () => {
          this.settlement.set(null);
          this.notify.success();
          this.changed.emit();
        },
        error: (e) => this.notify.error(e)
      });
  }

  private done(s: Settlement): void {
    this.settlement.set(s);
    this.notify.success();
    this.changed.emit();
  }

  private load(): void {
    this.api.settlements().subscribe((list) => {
      this.settlement.set(list.find((s) => s.employeeId === this.employee().id && s.status !== 'Reversed') ?? null);
      this.loaded.set(true);
    });
  }
}
