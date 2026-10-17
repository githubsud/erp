import { Component, inject, signal } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { HrSettings } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

const NUMBER_FIELDS: [keyof HrSettings, string][] = [
  ['gratuityDaysPerYear', 'settings.gratuityDays'],
  ['gratuityDivisor', 'settings.gratuityDivisor'],
  ['gratuityMinServiceMonths', 'settings.minService'],
  ['dailyRateDivisor', 'settings.dailyDivisor'],
  ['hoursPerDay', 'settings.hoursPerDay'],
  ['overtimeNormalRate', 'settings.otNormal'],
  ['overtimeNightRate', 'settings.otNight'],
  ['overtimeRestDayRate', 'settings.otRest'],
  ['overtimeHolidayRate', 'settings.otHoliday'],
  ['deductionCapPercent', 'settings.deductionCap'],
  ['minimumBasicWage', 'settings.minBasic'],
  ['minimumHousingAllowance', 'settings.minHousing'],
  ['minimumFoodAllowance', 'settings.minFood'],
  ['wpsDepositDays', 'settings.wpsDays'],
  ['sickFullPayDays', 'settings.sickFull'],
  ['sickHalfPayDays', 'settings.sickHalf'],
  ['sickMinServiceMonths', 'settings.sickMinService']
];

@Component({
  selector: 'app-hr-settings',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'nav.hrSettings' | t }}</h1></div>
    <form [formGroup]="form" (ngSubmit)="save()">
      <mat-card appearance="outlined" class="block">
        <div class="section-title">{{ 'settings.wpsSection' | t }}</div>
        <div class="form-grid">
          <mat-form-field>
            <mat-label>{{ 'settings.employerEid' | t }}</mat-label>
            <input matInput formControlName="employerEid" dir="ltr" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'settings.payerEid' | t }}</mat-label>
            <input matInput formControlName="payerEid" dir="ltr" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'settings.payerQid' | t }}</mat-label>
            <input matInput formControlName="payerQid" dir="ltr" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'settings.payerBank' | t }}</mat-label>
            <input matInput formControlName="payerBankShortName" dir="ltr" maxlength="4" />
          </mat-form-field>
          <mat-form-field class="full">
            <mat-label>{{ 'settings.payerIban' | t }}</mat-label>
            <input matInput formControlName="payerIban" dir="ltr" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'settings.sifVersion' | t }}</mat-label>
            <mat-select formControlName="sifFormatVersion">
              <mat-option value="1">1</mat-option>
              <mat-option value="0">0</mat-option>
            </mat-select>
          </mat-form-field>
        </div>

        <div class="section-title">{{ 'settings.payrollSection' | t }}</div>
        <p class="hint">{{ 'settings.verify' | t }}</p>
        <div class="form-grid">
          @for (f of numberFields; track f[0]) {
            <mat-form-field>
              <mat-label>{{ f[1] | t }}</mat-label>
              <input matInput type="number" [formControlName]="f[0]" dir="ltr" />
            </mat-form-field>
          }
          <mat-form-field>
            <mat-label>{{ 'settings.leaveBasis' | t }}</mat-label>
            <mat-select formControlName="leaveSalaryBasis">
              <mat-option value="Basic">{{ 'contract.basis.Basic' | t }}</mat-option>
              <mat-option value="BasicPlusFixedAllowances">{{ 'contract.basis.BasicPlusFixedAllowances' | t }}</mat-option>
            </mat-select>
          </mat-form-field>
        </div>
      </mat-card>
      @if (canEdit) {
        <div class="actions end"><button mat-flat-button [disabled]="form.invalid || busy()">{{ 'app.save' | t }}</button></div>
      }
    </form>
  `,
  styles: `
    .block { padding: 8px 20px 20px; }
    .hint { color: var(--mat-sys-on-surface-variant); margin-top: 0; }
    .end { justify-content: flex-end; margin-top: 16px; }
  `
})
export class HrSettingsPage {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly canEdit = inject(AuthService).can('hr.settings:manage');
  protected readonly numberFields = NUMBER_FIELDS;
  protected readonly busy = signal(false);

  protected readonly form = inject(FormBuilder).group({
    employerEid: [''],
    payerEid: [''],
    payerQid: [''],
    payerBankShortName: [''],
    payerIban: [''],
    sifFormatVersion: ['1'],
    gratuityDaysPerYear: [21, [Validators.required, Validators.min(21)]],
    gratuityDivisor: [30, Validators.required],
    gratuityMinServiceMonths: [12, Validators.required],
    dailyRateDivisor: [30, Validators.required],
    hoursPerDay: [8, Validators.required],
    overtimeNormalRate: [1.25, Validators.required],
    overtimeNightRate: [1.5, Validators.required],
    overtimeRestDayRate: [1.5, Validators.required],
    overtimeHolidayRate: [1.5, Validators.required],
    deductionCapPercent: [50, Validators.required],
    minimumBasicWage: [1000, Validators.required],
    minimumHousingAllowance: [500, Validators.required],
    minimumFoodAllowance: [300, Validators.required],
    wpsDepositDays: [7, Validators.required],
    sickFullPayDays: [14, Validators.required],
    sickHalfPayDays: [28, Validators.required],
    sickMinServiceMonths: [3, Validators.required],
    leaveSalaryBasis: ['Basic' as HrSettings['leaveSalaryBasis']]
  });

  constructor() {
    if (!this.canEdit) this.form.disable();
    this.api.hrSettings().subscribe((s) => this.form.patchValue(s));
  }

  save(): void {
    const raw = this.form.getRawValue();
    const body = { ...raw } as unknown as HrSettings;
    for (const [key] of NUMBER_FIELDS) (body as unknown as Record<string, unknown>)[key] = Number(raw[key as keyof typeof raw]);
    for (const key of ['employerEid', 'payerEid', 'payerQid', 'payerBankShortName', 'payerIban'] as const) body[key] = raw[key] || null;
    this.busy.set(true);
    this.api.updateHrSettings(body).subscribe({
      next: (s) => {
        this.busy.set(false);
        this.form.patchValue(s);
        this.notify.success();
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }
}
