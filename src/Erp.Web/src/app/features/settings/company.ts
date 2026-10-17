import { Component, inject, signal } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { blankToNull } from '../../core/dates';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

@Component({
  selector: 'app-company',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'nav.company' | t }}</h1></div>
    <form [formGroup]="form" (ngSubmit)="save()">
      <mat-card appearance="outlined" class="block">
        <div class="form-grid">
          <mat-form-field>
            <mat-label>{{ 'company.nameAr' | t }}</mat-label>
            <input matInput formControlName="nameAr" dir="rtl" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'company.nameEn' | t }}</mat-label>
            <input matInput formControlName="nameEn" dir="ltr" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'company.crNo' | t }}</mat-label>
            <input matInput formControlName="crNo" dir="ltr" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'company.establishmentId' | t }}</mat-label>
            <input matInput formControlName="establishmentId" dir="ltr" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'company.tin' | t }}</mat-label>
            <input matInput formControlName="tin" dir="ltr" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'company.foreignOwnership' | t }}</mat-label>
            <input matInput type="number" min="0" max="100" formControlName="foreignOwnershipPct" dir="ltr" />
          </mat-form-field>
          <mat-form-field class="full">
            <mat-label>{{ 'company.addressAr' | t }}</mat-label>
            <input matInput formControlName="addressAr" dir="rtl" />
          </mat-form-field>
          <mat-form-field class="full">
            <mat-label>{{ 'company.addressEn' | t }}</mat-label>
            <input matInput formControlName="addressEn" dir="ltr" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'company.phone' | t }}</mat-label>
            <input matInput formControlName="phone" dir="ltr" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'company.email' | t }}</mat-label>
            <input matInput formControlName="email" dir="ltr" />
          </mat-form-field>
        </div>
      </mat-card>
      @if (canEdit) {
        <div class="actions end"><button mat-flat-button [disabled]="form.invalid || busy()">{{ 'app.save' | t }}</button></div>
      }
    </form>
  `,
  styles: `
    .block { padding: 20px; }
    .end { justify-content: flex-end; margin-top: 16px; }
  `
})
export class CompanyPage {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly canEdit = inject(AuthService).can('core.company:manage');
  protected readonly busy = signal(false);
  protected readonly form = inject(FormBuilder).group({
    nameEn: ['', Validators.required],
    nameAr: ['', Validators.required],
    crNo: [''],
    establishmentId: [''],
    tin: [''],
    foreignOwnershipPct: [0, [Validators.min(0), Validators.max(100)]],
    addressEn: [''],
    addressAr: [''],
    phone: [''],
    email: ['']
  });

  constructor() {
    if (!this.canEdit) this.form.disable();
    this.api.company().subscribe((c) => this.form.patchValue(c));
  }

  save(): void {
    const v = blankToNull(this.form.getRawValue());
    this.busy.set(true);
    this.api.updateCompany({ ...v, foreignOwnershipPct: Number(v.foreignOwnershipPct ?? 0) } as never).subscribe({
      next: () => {
        this.busy.set(false);
        this.notify.success();
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }
}
