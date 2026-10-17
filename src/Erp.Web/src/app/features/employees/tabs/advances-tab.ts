import { Component, inject, input, OnInit, signal } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
import { map } from 'rxjs';
import { ApiService } from '../../../core/api.service';
import { AuthService } from '../../../core/auth.service';
import { todayIso } from '../../../core/dates';
import { Account, Advance, Employee } from '../../../core/models';
import { NotifyService } from '../../../core/notify.service';
import { SHARED } from '../../../shared/shared';

@Component({
  selector: 'app-advances-tab',
  imports: [...SHARED],
  template: `
    <div class="card-grid">
      <mat-card appearance="outlined" class="stat">
        <div class="label">{{ 'advance.outstanding' | t }}</div>
        <div class="value ltr">{{ outstanding() | money }}</div>
        <div class="hint">{{ 'app.qar' | t }}</div>
      </mat-card>
    </div>

    @if (auth.can('hr.payroll:post') && employee().status === 'Active') {
      <form [formGroup]="form" (ngSubmit)="add()" class="add">
        <div class="form-grid">
          <mat-form-field>
            <mat-label>{{ 'app.date' | t }}</mat-label>
            <input matInput type="date" formControlName="date" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'app.amount' | t }}</mat-label>
            <input matInput type="number" min="1" formControlName="amount" dir="ltr" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'advance.monthly' | t }}</mat-label>
            <input matInput type="number" min="1" formControlName="monthlyRecovery" dir="ltr" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'advance.paidFrom' | t }}</mat-label>
            <mat-select formControlName="paymentAccountId">
              @for (a of accounts(); track a.id) { <mat-option [value]="a.id">{{ a.code }} · {{ a | bi }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <mat-form-field class="full">
            <mat-label>{{ 'app.notes' | t }}</mat-label>
            <input matInput formControlName="notes" />
          </mat-form-field>
        </div>
        <div class="actions"><button mat-flat-button [disabled]="form.invalid"><mat-icon>add</mat-icon> {{ 'advance.new' | t }}</button></div>
      </form>
    }

    <div class="table-wrap">
      <table mat-table [dataSource]="advances()">
        <ng-container matColumnDef="no">
          <th mat-header-cell *matHeaderCellDef>#</th>
          <td mat-cell *matCellDef="let a" class="ltr">{{ a.advanceNo }}</td>
        </ng-container>
        <ng-container matColumnDef="date">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.date' | t }}</th>
          <td mat-cell *matCellDef="let a"><span class="ltr">{{ a.advanceDate | d }}</span></td>
        </ng-container>
        <ng-container matColumnDef="amount">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.amount' | t }}</th>
          <td mat-cell *matCellDef="let a" class="num ltr">{{ a.amount | money }}</td>
        </ng-container>
        <ng-container matColumnDef="monthly">
          <th mat-header-cell *matHeaderCellDef>{{ 'advance.monthly' | t }}</th>
          <td mat-cell *matCellDef="let a" class="num ltr">{{ a.monthlyRecovery | money }}</td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
      </table>
      @if (advances().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `,
  styles: `.add { margin-bottom: 16px; }`
})
export class AdvancesTab implements OnInit {
  readonly employee = input.required<Employee>();
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);

  protected readonly columns = ['no', 'date', 'amount', 'monthly'];
  protected readonly advances = signal<Advance[]>([]);
  protected readonly outstanding = signal(0);
  protected readonly accounts = signal<Account[]>([]);
  protected readonly form = inject(FormBuilder).group({
    date: [todayIso(), Validators.required],
    amount: [null as number | null, [Validators.required, Validators.min(1)]],
    monthlyRecovery: [null as number | null],
    paymentAccountId: ['', Validators.required],
    notes: ['']
  });

  ngOnInit(): void {
    this.load();
    if (this.auth.can('hr.payroll:post'))
      this.api
        .accounts()
        .pipe(map((l) => l.filter((a) => a.isPostable && (a.subtype === 'bank' || a.subtype === 'cash'))))
        .subscribe((l) => {
          this.accounts.set(l);
          const bank = l.find((a) => a.subtype === 'bank');
          if (bank) this.form.controls.paymentAccountId.setValue(bank.id);
        });
  }

  protected add(): void {
    const v = this.form.getRawValue();
    this.api
      .createAdvance(this.employee().id, { ...v, amount: Number(v.amount), monthlyRecovery: v.monthlyRecovery ? Number(v.monthlyRecovery) : null, notes: v.notes || null })
      .subscribe({
        next: () => {
          this.notify.success();
          this.form.patchValue({ amount: null, monthlyRecovery: null, notes: '' });
          this.load();
        },
        error: (e) => this.notify.error(e)
      });
  }

  private load(): void {
    this.api.advances(this.employee().id).subscribe((a) => this.advances.set(a));
    this.api.advanceBalance(this.employee().id).subscribe((b) => this.outstanding.set(b.outstanding));
  }
}
