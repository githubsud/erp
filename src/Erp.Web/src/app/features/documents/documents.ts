import { Component, inject, signal } from '@angular/core';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { blankToNull } from '../../core/dates';
import { CompanyDocument, ExpiringDocument } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

const COMPANY_DOC_TYPES = [
  'CommercialRegistration', 'CommercialLicense', 'MunicipalityLicense', 'EstablishmentCard', 'ChamberCertificate', 'TaxCard',
  'CivilDefence', 'Lease', 'Insurance', 'VehicleRegistration', 'Other'
];

@Component({
  selector: 'app-documents',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'nav.documents' | t }}</h1></div>
    <mat-tab-group mat-stretch-tabs="false">
      <mat-tab [label]="'document.expiringTitle' | t">
        <div class="tab">
          <div class="toolbar-row">
            <mat-form-field class="within">
              <mat-label>{{ 'document.within' | t }}</mat-label>
              <input matInput type="number" [formControl]="within" min="0" max="366" />
            </mat-form-field>
            <button mat-stroked-button (click)="loadExpiring()">{{ 'finance.run' | t }}</button>
          </div>
          <div class="table-wrap">
            <table mat-table [dataSource]="expiring()">
              <ng-container matColumnDef="owner">
                <th mat-header-cell *matHeaderCellDef>{{ 'document.owner' | t }}</th>
                <td mat-cell *matCellDef="let d">
                  @if (d.employeeId) {
                    <a [routerLink]="['/employees', d.employeeId]" [queryParams]="{ tab: 'documents' }">{{ d | bi:'ownerName' }}</a>
                  } @else {
                    <mat-icon class="small">apartment</mat-icon> {{ d | bi:'ownerName' }}
                  }
                </td>
              </ng-container>
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
              <ng-container matColumnDef="status">
                <th mat-header-cell *matHeaderCellDef>{{ 'document.daysLeft' | t }}</th>
                <td mat-cell *matCellDef="let d">
                  <span class="chip" [class.bad]="d.status === 'Expired'" [class.warn]="d.status === 'Expiring'">{{ d.daysLeft }} · {{ 'document.status.' + d.status | t }}</span>
                </td>
              </ng-container>
              <tr mat-header-row *matHeaderRowDef="expiringColumns"></tr>
              <tr mat-row *matRowDef="let row; columns: expiringColumns"></tr>
            </table>
            @if (expiring().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
          </div>
        </div>
      </mat-tab>

      <mat-tab [label]="'document.companyTitle' | t">
        <div class="tab">
          @if (auth.can('hr.documents:manage')) {
            <mat-card appearance="outlined" class="form-card">
              <form [formGroup]="form" (ngSubmit)="save()">
                <div class="form-grid">
                  <mat-form-field>
                    <mat-label>{{ 'document.type' | t }}</mat-label>
                    <mat-select formControlName="docType">
                      @for (t of companyTypes; track t) { <mat-option [value]="t">{{ 'docType.' + t | t }}</mat-option> }
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
                  <mat-form-field class="full">
                    <mat-label>{{ 'app.notes' | t }}</mat-label>
                    <input matInput formControlName="notes" />
                  </mat-form-field>
                </div>
                <div class="actions">
                  @if (editingId()) { <button mat-button type="button" (click)="reset()">{{ 'app.cancel' | t }}</button> }
                  <button mat-flat-button [disabled]="form.invalid">{{ (editingId() ? 'app.save' : 'document.add') | t }}</button>
                </div>
              </form>
            </mat-card>
          }
          <div class="table-wrap">
            <table mat-table [dataSource]="company()">
              <ng-container matColumnDef="type">
                <th mat-header-cell *matHeaderCellDef>{{ 'document.type' | t }}</th>
                <td mat-cell *matCellDef="let d">{{ d | bi }}</td>
              </ng-container>
              <ng-container matColumnDef="no">
                <th mat-header-cell *matHeaderCellDef>{{ 'document.no' | t }}</th>
                <td mat-cell *matCellDef="let d" class="ltr">{{ d.docNo }}</td>
              </ng-container>
              <ng-container matColumnDef="expiry">
                <th mat-header-cell *matHeaderCellDef>{{ 'document.expiryDate' | t }}</th>
                <td mat-cell *matCellDef="let d"><span class="ltr">{{ d.expiryDate | d }}</span></td>
              </ng-container>
              <ng-container matColumnDef="actions">
                <th mat-header-cell *matHeaderCellDef></th>
                <td mat-cell *matCellDef="let d">
                  @if (auth.can('hr.documents:manage')) { <button mat-icon-button (click)="edit(d)"><mat-icon>edit</mat-icon></button> }
                </td>
              </ng-container>
              <tr mat-header-row *matHeaderRowDef="companyColumns"></tr>
              <tr mat-row *matRowDef="let row; columns: companyColumns"></tr>
            </table>
            @if (company().length === 0) { <div class="empty">{{ 'app.none' | t }}</div> }
          </div>
        </div>
      </mat-tab>
    </mat-tab-group>
  `,
  styles: `
    .tab { padding: 20px 4px; }
    .within { width: 160px; }
    .form-card { padding: 16px 20px; margin-bottom: 16px; }
    .small { font-size: 18px; width: 18px; height: 18px; vertical-align: middle; }
    a { color: inherit; }
  `
})
export class DocumentsPage {
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);

  protected readonly companyTypes = COMPANY_DOC_TYPES;
  protected readonly expiringColumns = ['owner', 'type', 'no', 'expiry', 'status'];
  protected readonly companyColumns = ['type', 'no', 'expiry', 'actions'];
  protected readonly within = new FormControl(90, { nonNullable: true });
  protected readonly expiring = signal<ExpiringDocument[]>([]);
  protected readonly company = signal<CompanyDocument[]>([]);
  protected readonly editingId = signal<string | null>(null);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    docType: ['CommercialRegistration', Validators.required],
    docNo: [''],
    issueDate: [''],
    expiryDate: ['', Validators.required],
    notes: ['']
  });

  constructor() {
    this.loadExpiring();
    this.loadCompany();
  }

  loadExpiring(): void {
    this.api.expiringDocuments(this.within.value).subscribe((d) => this.expiring.set(d));
  }

  protected edit(d: CompanyDocument): void {
    this.editingId.set(d.id);
    this.form.setValue({ docType: d.docType, docNo: d.docNo ?? '', issueDate: d.issueDate ?? '', expiryDate: d.expiryDate ?? '', notes: d.notes ?? '' });
  }

  protected reset(): void {
    this.editingId.set(null);
    this.form.reset();
  }

  protected save(): void {
    this.api.saveCompanyDocument(this.editingId(), blankToNull(this.form.getRawValue())).subscribe({
      next: () => {
        this.notify.success();
        this.reset();
        this.loadCompany();
        this.loadExpiring();
      },
      error: (e) => this.notify.error(e)
    });
  }

  private loadCompany(): void {
    this.api.companyDocuments().subscribe((d) => this.company.set(d));
  }
}
