import { Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { Employee, OvertimeType, PayComponent, PayrollInput } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';
import { I18nService } from '../../core/i18n/i18n.service';
import { currentMonth } from './payroll-list';

@Component({
  selector: 'app-payroll-inputs',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'inputs.title' | t }}</h1>
      <mat-form-field class="month">
        <mat-label>{{ 'payroll.month' | t }}</mat-label>
        <input matInput type="month" [formControl]="month" />
      </mat-form-field>
    </div>

    @if (auth.can('hr.payroll:prepare')) {
      <mat-card appearance="outlined" class="form-card">
        <form [formGroup]="form" (ngSubmit)="add()">
          <div class="form-grid">
            <mat-form-field>
              <mat-label>{{ 'inputs.employee' | t }}</mat-label>
              <mat-select formControlName="employeeId">
                @for (e of employees(); track e.id) { <mat-option [value]="e.id">{{ e.empNo }} · {{ e | bi }}</mat-option> }
              </mat-select>
            </mat-form-field>
            <mat-form-field>
              <mat-label>{{ 'inputs.component' | t }}</mat-label>
              <mat-select formControlName="componentCode">
                @for (c of variableComponents(); track c.code) { <mat-option [value]="c.code">{{ c | bi }}</mat-option> }
              </mat-select>
            </mat-form-field>
            @if (isOvertime()) {
              <mat-form-field>
                <mat-label>{{ 'inputs.overtimeType' | t }}</mat-label>
                <mat-select formControlName="overtimeType">
                  @for (o of overtimeTypes; track o) { <mat-option [value]="o">{{ 'ot.' + o | t }}</mat-option> }
                </mat-select>
              </mat-form-field>
              <mat-form-field>
                <mat-label>{{ 'inputs.hours' | t }}</mat-label>
                <input matInput type="number" min="0.25" step="0.25" formControlName="hours" dir="ltr" />
              </mat-form-field>
            } @else {
              <mat-form-field>
                <mat-label>{{ 'app.amount' | t }}</mat-label>
                <input matInput type="number" min="0.01" formControlName="amount" dir="ltr" />
              </mat-form-field>
            }
            <mat-form-field>
              <mat-label>{{ 'app.notes' | t }}</mat-label>
              <input matInput formControlName="notes" />
            </mat-form-field>
          </div>
          <div class="actions"><button mat-flat-button [disabled]="!valid()"><mat-icon>add</mat-icon> {{ 'inputs.new' | t }}</button></div>
        </form>
      </mat-card>
    }

    <div class="table-wrap">
      <table mat-table [dataSource]="inputs()">
        <ng-container matColumnDef="employee">
          <th mat-header-cell *matHeaderCellDef>{{ 'inputs.employee' | t }}</th>
          <td mat-cell *matCellDef="let i">{{ employeeName(i.employeeId) }}</td>
        </ng-container>
        <ng-container matColumnDef="component">
          <th mat-header-cell *matHeaderCellDef>{{ 'inputs.component' | t }}</th>
          <td mat-cell *matCellDef="let i">{{ componentName(i.componentCode) }} @if (i.overtimeType) { · {{ 'ot.' + i.overtimeType | t }} }</td>
        </ng-container>
        <ng-container matColumnDef="value">
          <th mat-header-cell *matHeaderCellDef class="num">{{ 'app.amount' | t }} / {{ 'inputs.hours' | t }}</th>
          <td mat-cell *matCellDef="let i" class="num ltr">{{ i.amount !== null ? (i.amount | money) : i.hours + ' h' }}</td>
        </ng-container>
        <ng-container matColumnDef="notes">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.notes' | t }}</th>
          <td mat-cell *matCellDef="let i">@if (i.source === 'Attendance') { <span class="chip info">{{ 'nav.attendance' | t }}</span> } @if (i.source === 'Expense') { <span class="chip info">{{ 'me.nav.expenses' | t }}</span> } {{ i.notes }}</td>
        </ng-container>
        <ng-container matColumnDef="actions">
          <th mat-header-cell *matHeaderCellDef></th>
          <td mat-cell *matCellDef="let i">
            @if (auth.can('hr.payroll:prepare')) { <button mat-icon-button (click)="remove(i)"><mat-icon>delete</mat-icon></button> }
          </td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
      </table>
      @if (inputs().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `,
  styles: `
    .month { width: 200px; }
    .form-card { padding: 16px 20px; margin-bottom: 16px; }
  `
})
export class PayrollInputsPage {
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  private readonly i18n = inject(I18nService);

  protected readonly columns = ['employee', 'component', 'value', 'notes', 'actions'];
  protected readonly overtimeTypes: OvertimeType[] = ['Normal', 'Night', 'RestDay', 'Holiday'];
  protected readonly month = new FormControl(currentMonth(), { nonNullable: true, validators: Validators.required });
  protected readonly employees = signal<Employee[]>([]);
  protected readonly components = signal<PayComponent[]>([]);
  protected readonly inputs = signal<PayrollInput[]>([]);
  protected readonly variableComponents = computed(() => this.components().filter((c) => !c.isFixed && c.isActive));

  protected readonly form = inject(FormBuilder).group({
    employeeId: ['', Validators.required],
    componentCode: ['OVERTIME', Validators.required],
    overtimeType: ['Normal' as OvertimeType],
    hours: [null as number | null],
    amount: [null as number | null],
    notes: ['']
  });
  private readonly componentCode = signal('OVERTIME');
  private readonly formValue = signal(this.form.getRawValue());
  protected readonly isOvertime = computed(() => this.components().find((c) => c.code === this.componentCode())?.wpsField === 'Overtime');
  protected readonly valid = computed(() => {
    const v = this.formValue();
    if (!v.employeeId || !v.componentCode) return false;
    return this.isOvertime() ? (v.hours ?? 0) > 0 && !!v.overtimeType : (v.amount ?? 0) > 0;
  });

  constructor() {
    this.form.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => {
      this.formValue.set(this.form.getRawValue());
      this.componentCode.set(this.form.controls.componentCode.value ?? '');
    });
    this.month.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => this.load());
    this.api.employees({ status: 'Active', pageSize: 200 }).subscribe((r) => this.employees.set(r.items));
    this.api.payComponents().subscribe((c) => this.components.set(c));
    this.load();
  }

  protected employeeName(id: string): string {
    const e = this.employees().find((x) => x.id === id);
    return e ? `${e.empNo} · ${this.i18n.pick(e)}` : id;
  }

  protected componentName(code: string): string {
    const c = this.components().find((x) => x.code === code);
    return c ? this.i18n.pick(c) : code;
  }

  protected add(): void {
    const v = this.form.getRawValue();
    const [year, month] = this.month.value.split('-').map(Number);
    const overtime = this.isOvertime();
    this.api
      .createPayrollInput({
        employeeId: v.employeeId,
        year,
        month,
        componentCode: v.componentCode,
        overtimeType: overtime ? v.overtimeType : null,
        hours: overtime ? Number(v.hours) : null,
        amount: overtime ? null : Number(v.amount),
        notes: v.notes || null
      })
      .subscribe({
        next: () => {
          this.notify.success();
          this.form.patchValue({ hours: null, amount: null, notes: '' });
          this.load();
        },
        error: (e) => this.notify.error(e)
      });
  }

  protected remove(i: PayrollInput): void {
    this.api.deletePayrollInput(i.id).subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  private load(): void {
    const [year, month] = this.month.value.split('-').map(Number);
    if (!year || !month) return;
    this.api.payrollInputs(year, month).subscribe((i) => this.inputs.set(i));
  }
}
