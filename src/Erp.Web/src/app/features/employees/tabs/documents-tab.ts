import { Component, inject, input, OnInit, signal } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
import { ApiService } from '../../../core/api.service';
import { AuthService } from '../../../core/auth.service';
import { blankToNull, daysBetween, todayIso } from '../../../core/dates';
import { Employee, EmployeeDocument } from '../../../core/models';
import { NotifyService } from '../../../core/notify.service';
import { SHARED } from '../../../shared/shared';

export const EMPLOYEE_DOC_TYPES = ['Qid', 'Passport', 'ResidencePermit', 'WorkPermit', 'HealthCard', 'DrivingLicence', 'ProfessionalLicence', 'Other'];

@Component({
  selector: 'app-documents-tab',
  imports: [...SHARED],
  template: `
    @if (auth.can('hr.documents:manage')) {
      <form [formGroup]="form" (ngSubmit)="add()" class="add">
        <div class="form-grid">
          <mat-form-field>
            <mat-label>{{ 'document.type' | t }}</mat-label>
            <mat-select formControlName="docType">
              @for (t of types; track t) { <mat-option [value]="t">{{ 'docType.' + t | t }}</mat-option> }
            </mat-select>
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'document.no' | t }}</mat-label>
            <input matInput formControlName="docNo" dir="ltr" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'document.issueDate' | t }}</mat-label>
            <input matInput type="date" formControlName="issueDate" />
          </mat-form-field>
          <mat-form-field>
            <mat-label>{{ 'document.expiryDate' | t }}</mat-label>
            <input matInput type="date" formControlName="expiryDate" />
          </mat-form-field>
        </div>
        <div class="actions"><button mat-flat-button [disabled]="form.invalid"><mat-icon>add</mat-icon> {{ 'document.add' | t }}</button></div>
      </form>
    }

    <div class="table-wrap">
      <table mat-table [dataSource]="docs()">
        <ng-container matColumnDef="type">
          <th mat-header-cell *matHeaderCellDef>{{ 'document.type' | t }}</th>
          <td mat-cell *matCellDef="let d">{{ 'docType.' + d.docType | t }}</td>
        </ng-container>
        <ng-container matColumnDef="no">
          <th mat-header-cell *matHeaderCellDef>{{ 'document.no' | t }}</th>
          <td mat-cell *matCellDef="let d" class="ltr">{{ d.docNo }}</td>
        </ng-container>
        <ng-container matColumnDef="expiry">
          <th mat-header-cell *matHeaderCellDef>{{ 'document.expiryDate' | t }}</th>
          <td mat-cell *matCellDef="let d"><span class="ltr">{{ d.expiryDate | d }}</span></td>
        </ng-container>
        <ng-container matColumnDef="days">
          <th mat-header-cell *matHeaderCellDef>{{ 'document.daysLeft' | t }}</th>
          <td mat-cell *matCellDef="let d">
            @if (d.expiryDate) {
              <span class="chip" [class.bad]="left(d) < 0" [class.warn]="left(d) >= 0 && left(d) <= 90" [class.ok]="left(d) > 90">{{ left(d) }}</span>
            }
          </td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
      </table>
      @if (docs().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
    </div>
  `,
  styles: `.add { margin-bottom: 16px; }`
})
export class DocumentsTab implements OnInit {
  readonly employee = input.required<Employee>();
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);

  protected readonly types = EMPLOYEE_DOC_TYPES;
  protected readonly columns = ['type', 'no', 'expiry', 'days'];
  protected readonly docs = signal<EmployeeDocument[]>([]);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    docType: ['Qid', Validators.required],
    docNo: [''],
    issueDate: [''],
    expiryDate: ['', Validators.required]
  });

  ngOnInit(): void {
    this.load();
  }

  protected left(d: EmployeeDocument): number {
    return d.expiryDate ? daysBetween(todayIso(), d.expiryDate) : 0;
  }

  protected add(): void {
    this.api.addEmployeeDocument(this.employee().id, blankToNull(this.form.getRawValue())).subscribe({
      next: () => {
        this.notify.success();
        this.form.reset({ docType: 'Qid', docNo: '', issueDate: '', expiryDate: '' });
        this.load();
      },
      error: (e) => this.notify.error(e)
    });
  }

  private load(): void {
    this.api.employeeDocuments(this.employee().id).subscribe((d) => this.docs.set(d));
  }
}
