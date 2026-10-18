import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { ApiService } from '../../../core/api.service';
import { AuthService } from '../../../core/auth.service';
import { Contract, Employee, PayComponent } from '../../../core/models';
import { NotifyService } from '../../../core/notify.service';
import { SHARED } from '../../../shared/shared';

@Component({
  selector: 'app-contract-tab',
  imports: [...SHARED],
  template: `
    @if (!canView) {
      <div class="error-panel">{{ 'employee.masked' | t }}</div>
    } @else {
      @if (active(); as c) {
        <mat-card appearance="outlined" class="current">
          <div class="section-title">{{ 'contract.current' | t }}</div>
          <div class="facts">
            <span>{{ 'contract.startDate' | t }}: <b class="ltr">{{ c.startDate | d }}</b></span>
            <span>{{ 'contract.type' | t }}: <b>{{ 'contract.type.' + c.contractType | t }}</b></span>
            <span>{{ 'contract.gratuityBasis' | t }}: <b>{{ 'contract.basis.' + c.gratuityBasis | t }}</b></span>
            @if (c.accommodationProvided) { <span class="chip info">{{ 'contract.accommodation' | t }}</span> }
            @if (c.foodProvided) { <span class="chip info">{{ 'contract.food' | t }}</span> }
            @if (c.annualAirTicketAmount) { <span>{{ 'contract.airTicket' | t }}: <b class="ltr">{{ c.annualAirTicketAmount | money }}</b></span> }
          </div>
          <table class="components">
            @for (x of c.components; track x.code) {
              <tr><td>{{ x | bi }}</td><td class="num ltr">{{ x.amount | money }}</td></tr>
            }
            <tr class="total"><td>{{ 'contract.monthlyGross' | t }}</td><td class="num ltr">{{ c.monthlyGross | money }}</td></tr>
          </table>
        </mat-card>
      } @else {
        <div class="error-panel">{{ 'contract.none' | t }}</div>
      }

      @if (auth.can('hr.contracts:manage') && employee().status === 'Active') {
        @if (!showForm()) {
          <button mat-stroked-button (click)="showForm.set(true)"><mat-icon>add</mat-icon> {{ 'contract.new' | t }}</button>
        } @else {
          <mat-card appearance="outlined" class="form-card">
            <div class="section-title">{{ 'contract.new' | t }}</div>
            <form [formGroup]="form" (ngSubmit)="save()">
              <div class="form-grid">
                <mat-form-field>
                  <mat-label>{{ 'contract.startDate' | t }}</mat-label>
                  <input matInput type="date" formControlName="startDate" />
                </mat-form-field>
                <mat-form-field>
                  <mat-label>{{ 'contract.type' | t }}</mat-label>
                  <mat-select formControlName="contractType">
                    <mat-option value="Unlimited">{{ 'contract.type.Unlimited' | t }}</mat-option>
                    <mat-option value="Limited">{{ 'contract.type.Limited' | t }}</mat-option>
                  </mat-select>
                </mat-form-field>
                @if (form.controls.contractType.value === 'Limited') {
                  <mat-form-field>
                    <mat-label>{{ 'contract.endDate' | t }}</mat-label>
                    <input matInput type="date" formControlName="endDate" />
                  </mat-form-field>
                }
                <mat-form-field>
                  <mat-label>{{ 'contract.probation' | t }}</mat-label>
                  <input matInput type="number" min="0" max="6" formControlName="probationMonths" />
                </mat-form-field>
                <mat-form-field>
                  <mat-label>{{ 'contract.gratuityBasis' | t }}</mat-label>
                  <mat-select formControlName="gratuityBasis">
                    <mat-option value="Basic">{{ 'contract.basis.Basic' | t }}</mat-option>
                    <mat-option value="BasicPlusFixedAllowances">{{ 'contract.basis.BasicPlusFixedAllowances' | t }}</mat-option>
                  </mat-select>
                </mat-form-field>
                <mat-form-field>
                  <mat-label>{{ 'contract.gratuityDays' | t }}</mat-label>
                  <input matInput type="number" formControlName="gratuityDaysPerYear" />
                </mat-form-field>
                <mat-form-field>
                  <mat-label>{{ 'contract.leaveDays' | t }}</mat-label>
                  <input matInput type="number" formControlName="annualLeaveDays" />
                </mat-form-field>
                <mat-form-field>
                  <mat-label>{{ 'contract.airTicket' | t }}</mat-label>
                  <input matInput type="number" min="0" formControlName="annualAirTicketAmount" dir="ltr" />
                </mat-form-field>
                <mat-slide-toggle formControlName="accommodationProvided">{{ 'contract.accommodation' | t }}</mat-slide-toggle>
                <mat-slide-toggle formControlName="foodProvided">{{ 'contract.food' | t }}</mat-slide-toggle>
              </div>

              <div class="section-title">{{ 'contract.components' | t }}</div>
              <div class="form-grid">
                @for (c of fixedComponents(); track c.code) {
                  <mat-form-field>
                    <mat-label>{{ c | bi }}</mat-label>
                    <input matInput type="number" min="0" [formControl]="amount(c.code)" dir="ltr" />
                    <span matTextSuffix>&nbsp;{{ 'app.qar' | t }}</span>
                  </mat-form-field>
                }
              </div>
              <div class="actions end">
                <button mat-button type="button" (click)="showForm.set(false)">{{ 'app.cancel' | t }}</button>
                <button mat-flat-button type="submit" [disabled]="form.invalid || busy()">{{ 'app.save' | t }}</button>
              </div>
            </form>
          </mat-card>
        }
      }

      @if (history().length > 1) {
        <div class="section-title">{{ 'contract.history' | t }}</div>
        <div class="table-wrap">
          <table mat-table [dataSource]="history()">
            <ng-container matColumnDef="start">
              <th mat-header-cell *matHeaderCellDef>{{ 'contract.startDate' | t }}</th>
              <td mat-cell *matCellDef="let c" class="ltr">{{ c.startDate | d }}</td>
            </ng-container>
            <ng-container matColumnDef="end">
              <th mat-header-cell *matHeaderCellDef>{{ 'contract.endDate' | t }}</th>
              <td mat-cell *matCellDef="let c" class="ltr">{{ c.endDate | d }}</td>
            </ng-container>
            <ng-container matColumnDef="gross">
              <th mat-header-cell *matHeaderCellDef>{{ 'contract.monthlyGross' | t }}</th>
              <td mat-cell *matCellDef="let c" class="num ltr">{{ c.monthlyGross | money }}</td>
            </ng-container>
            <ng-container matColumnDef="status">
              <th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
              <td mat-cell *matCellDef="let c"><span class="chip" [class.ok]="c.status === 'Active'">{{ c.status }}</span></td>
            </ng-container>
            <tr mat-header-row *matHeaderRowDef="['start', 'end', 'gross', 'status']"></tr>
            <tr mat-row *matRowDef="let row; columns: ['start', 'end', 'gross', 'status']"></tr>
          </table>
        </div>
      }
    }
  `,
  styles: `
    .current, .form-card { padding: 4px 20px 20px; margin-bottom: 16px; }
    .facts { display: flex; flex-wrap: wrap; gap: 8px 20px; align-items: center; margin-bottom: 12px; }
    .components { border-collapse: collapse; min-width: min(420px, 100%); }
    .components td { padding: 6px 0; border-bottom: 1px solid var(--mat-sys-outline-variant); }
    .components td.num { padding-inline-start: 32px; }
    .total td { font-weight: 700; border-bottom: none; }
    .end { justify-content: flex-end; }
  `
})
export class ContractTab implements OnInit {
  readonly employee = input.required<Employee>();
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);

  protected readonly canView = this.auth.can('hr.sensitive:view');
  protected readonly history = signal<Contract[]>([]);
  protected readonly active = computed(() => this.history().find((c) => c.status === 'Active') ?? null);
  protected readonly fixedComponents = signal<PayComponent[]>([]);
  protected readonly showForm = signal(false);
  protected readonly busy = signal(false);
  private readonly amounts = new Map<string, FormControl<number | null>>();

  protected readonly form = inject(FormBuilder).group({
    startDate: ['', Validators.required],
    endDate: [''],
    contractType: ['Unlimited', Validators.required],
    probationMonths: [0, [Validators.min(0), Validators.max(6)]],
    accommodationProvided: [false],
    foodProvided: [false],
    gratuityBasis: ['Basic'],
    gratuityDaysPerYear: [null as number | null],
    annualLeaveDays: [null as number | null],
    annualAirTicketAmount: [null as number | null]
  });

  ngOnInit(): void {
    if (!this.canView) return;
    this.load();
    this.api.payComponents().subscribe((list) => this.fixedComponents.set(list.filter((c) => c.isFixed && c.kind === 'Earning' && c.isActive)));
  }

  protected amount(code: string): FormControl<number | null> {
    let control = this.amounts.get(code);
    if (!control) {
      const current = this.active()?.components.find((c) => c.code === code)?.amount ?? null;
      control = new FormControl<number | null>(current);
      this.amounts.set(code, control);
    }
    return control;
  }

  protected save(): void {
    const v = this.form.getRawValue();
    const components = [...this.amounts.entries()]
      .filter(([, c]) => (c.value ?? 0) > 0)
      .map(([componentCode, c]) => ({ componentCode, amount: Number(c.value) }));
    this.busy.set(true);
    this.api
      .createContract(this.employee().id, {
        ...v,
        endDate: v.contractType === 'Limited' && v.endDate ? v.endDate : null,
        probationMonths: Number(v.probationMonths ?? 0),
        gratuityDaysPerYear: v.gratuityDaysPerYear || null,
        annualLeaveDays: v.annualLeaveDays || null,
        annualAirTicketAmount: v.annualAirTicketAmount ? Number(v.annualAirTicketAmount) : null,
        components
      })
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.showForm.set(false);
          this.amounts.clear();
          this.notify.success();
          this.load();
        },
        error: (e) => {
          this.busy.set(false);
          this.notify.error(e);
        }
      });
  }

  private load(): void {
    this.api.contracts(this.employee().id).subscribe((list) => {
      this.history.set(list);
      if (!list.length) this.form.patchValue({ startDate: this.employee().joiningDate });
    });
  }
}
