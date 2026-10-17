import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { ApiService } from '../../core/api.service';
import { blankToNull, todayIso } from '../../core/dates';
import { I18nService } from '../../core/i18n/i18n.service';
import { Department, EmployeeRequest, Gender, SocialInsuranceScheme } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { NATIONALITIES, SHARED } from '../../shared/shared';

@Component({
  selector: 'app-employee-form',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <a mat-icon-button [routerLink]="id() ? ['/employees', id()] : '/employees'"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
      <h1>{{ (id() ? 'employee.edit' : 'employee.new') | t }}</h1>
    </div>
    @if (masked()) {
      <div class="error-panel">{{ 'employee.masked' | t }}</div>
    }

    <form [formGroup]="form" (ngSubmit)="save()">
      <mat-card appearance="outlined" class="block">
        <div class="section-title">{{ 'employee.sectionPersonal' | t }}</div>
        <div class="form-grid">
          <mat-form-field>
            <mat-label>{{ 'employee.nameAr' | t }}</mat-label>
            <input matInput formControlName="nameAr" dir="rtl" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'employee.nameEn' | t }}</mat-label>
            <input matInput formControlName="nameEn" dir="ltr" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'employee.nationality' | t }}</mat-label>
            <mat-select formControlName="nationality">
              @for (n of nationalities; track n.code) {
                <mat-option [value]="n.code">{{ i18n.isArabic() ? n.ar : n.en }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'employee.gender' | t }}</mat-label>
            <mat-select formControlName="gender">
              <mat-option value="Male">{{ 'employee.male' | t }}</mat-option>
              <mat-option value="Female">{{ 'employee.female' | t }}</mat-option>
            </mat-select>
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'employee.dob' | t }}</mat-label>
            <input matInput type="date" formControlName="dateOfBirth" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'employee.mobile' | t }}</mat-label>
            <input matInput formControlName="mobile" dir="ltr" placeholder="+974" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'employee.email' | t }}</mat-label>
            <input matInput formControlName="email" dir="ltr" />
          </mat-form-field>
        </div>

        <div class="section-title">{{ 'employee.sectionEmployment' | t }}</div>
        <div class="form-grid">
          <mat-form-field>
            <mat-label>{{ 'employee.empNo' | t }}</mat-label>
            <input matInput formControlName="empNo" dir="ltr" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'employee.joiningDate' | t }}</mat-label>
            <input matInput type="date" formControlName="joiningDate" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'employee.department' | t }}</mat-label>
            <mat-select formControlName="departmentId">
              <mat-option [value]="null">—</mat-option>
              @for (d of departments(); track d.id) {
                <mat-option [value]="d.id">{{ d | bi }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'employee.jobTitleAr' | t }}</mat-label>
            <input matInput formControlName="jobTitleAr" dir="rtl" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'employee.jobTitleEn' | t }}</mat-label>
            <input matInput formControlName="jobTitleEn" dir="ltr" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'employee.socialInsurance' | t }}</mat-label>
            <mat-select formControlName="socialInsuranceScheme">
              @for (s of schemes; track s) {
                <mat-option [value]="s">{{ 'employee.scheme.' + s | t }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'employee.openingGratuity' | t }}</mat-label>
            <input matInput type="number" formControlName="openingGratuityLiability" dir="ltr" />
          </mat-form-field>
        </div>

        <div class="section-title">{{ 'employee.sectionIdentity' | t }}</div>
        <div class="form-grid">
          <mat-form-field>
            <mat-label>{{ 'employee.qid' | t }}</mat-label>
            <input matInput formControlName="qid" dir="ltr" maxlength="11" inputmode="numeric" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'employee.visaId' | t }}</mat-label>
            <input matInput formControlName="visaId" dir="ltr" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'employee.passport' | t }}</mat-label>
            <input matInput formControlName="passportNo" dir="ltr" />
          </mat-form-field>
          <mat-form-field class="iban">
            <mat-label>{{ 'employee.iban' | t }}</mat-label>
            <input matInput formControlName="iban" dir="ltr" placeholder="QA00XXXX000000000000000000000" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'employee.bank' | t }}</mat-label>
            <input matInput formControlName="bankShortName" dir="ltr" maxlength="4" />
          </mat-form-field>
          <mat-slide-toggle formControlName="inWps">{{ 'employee.inWps' | t }}</mat-slide-toggle>
        </div>
      </mat-card>

      <div class="actions end">
        <button mat-flat-button type="submit" [disabled]="form.invalid || busy() || masked()">{{ 'app.save' | t }}</button>
      </div>
    </form>
  `,
  styles: `
    .block { padding: 8px 20px 20px; }
    .end { justify-content: flex-end; margin-top: 16px; }
  `
})
export class EmployeeFormPage implements OnInit {
  readonly id = input<string>();
  protected readonly i18n = inject(I18nService);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly notify = inject(NotifyService);

  protected readonly nationalities = NATIONALITIES;
  protected readonly schemes: SocialInsuranceScheme[] = ['None', 'Grsia', 'GccHome'];
  protected readonly departments = signal<Department[]>([]);
  protected readonly busy = signal(false);
  private readonly sensitiveMasked = signal(false);
  protected readonly masked = computed(() => this.sensitiveMasked());

  protected readonly form = inject(FormBuilder).group({
    empNo: ['', Validators.required],
    nameEn: [''],
    nameAr: [''],
    nationality: ['', Validators.required],
    gender: ['Male' as Gender, Validators.required],
    dateOfBirth: [''],
    joiningDate: [todayIso(), Validators.required],
    departmentId: [null as string | null],
    jobTitleEn: [''],
    jobTitleAr: [''],
    mobile: [''],
    email: [''],
    qid: ['', Validators.pattern(/^\d{11}$/)],
    visaId: [''],
    passportNo: [''],
    bankShortName: [''],
    iban: [''],
    inWps: [true],
    socialInsuranceScheme: ['None' as SocialInsuranceScheme],
    openingGratuityLiability: [0]
  });

  ngOnInit(): void {
    this.api.departments().subscribe((d) => this.departments.set(d));
    const id = this.id();
    if (!id) return;
    this.api.employee(id).subscribe((e) => {
      this.sensitiveMasked.set(e.sensitiveMasked);
      this.form.patchValue({ ...e, dateOfBirth: e.dateOfBirth ?? '', openingGratuityLiability: e.openingGratuityLiability ?? 0 });
      this.form.controls.empNo.disable();
    });
  }

  save(): void {
    if (this.form.invalid) return;
    const value = blankToNull(this.form.getRawValue()) as unknown as EmployeeRequest;
    value.openingGratuityLiability = Number(value.openingGratuityLiability ?? 0);
    this.busy.set(true);
    const id = this.id();
    const request = id ? this.api.updateEmployee(id, value) : this.api.createEmployee(value);
    request.subscribe({
      next: (e) => {
        this.notify.success();
        void this.router.navigate(['/employees', e.id]);
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }
}
