import { Component, inject, input, OnInit, signal } from '@angular/core';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { Employee } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { NATIONALITIES, SHARED } from '../../shared/shared';
import { I18nService } from '../../core/i18n/i18n.service';
import { AdvancesTab } from './tabs/advances-tab';
import { ContractTab } from './tabs/contract-tab';
import { DocumentsTab } from './tabs/documents-tab';
import { LeaveTab } from './tabs/leave-tab';
import { SettlementTab } from './tabs/settlement-tab';

const TABS = ['profile', 'contract', 'documents', 'leave', 'advances', 'settlement'];

@Component({
  selector: 'app-employee-detail',
  imports: [...SHARED, ContractTab, DocumentsTab, LeaveTab, AdvancesTab, SettlementTab],
  template: `
    @if (employee(); as e) {
      <div class="page-header">
        <a mat-icon-button routerLink="/employees"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
        <div class="title">
          <h1>{{ e | bi }}</h1>
          <div class="sub">
            <span class="ltr">{{ e.empNo }}</span> · {{ e | bi:'jobTitle' }} ·
            <span class="chip" [class.ok]="e.status === 'Active'">{{ 'employee.status.' + e.status | t }}</span>
          </div>
        </div>
        @if (auth.can('hr.employees:manage')) {
          <a mat-stroked-button [routerLink]="['/employees', e.id, 'edit']"><mat-icon>edit</mat-icon> {{ 'app.edit' | t }}</a>
        }
      </div>

      <mat-tab-group [selectedIndex]="tabIndex()" (selectedIndexChange)="tabIndex.set($event)" mat-stretch-tabs="false">
        <mat-tab [label]="'tab.profile' | t">
          <div class="tab">
            @if (e.sensitiveMasked) {
              <div class="error-panel">{{ 'employee.masked' | t }}</div>
            }
            <dl class="facts">
              <dt>{{ 'employee.nameAr' | t }}</dt><dd>{{ e.nameAr }}</dd>
              <dt>{{ 'employee.nameEn' | t }}</dt><dd class="ltr">{{ e.nameEn }}</dd>
              <dt>{{ 'employee.nationality' | t }}</dt><dd>{{ nationality(e.nationality) }}</dd>
              <dt>{{ 'employee.gender' | t }}</dt><dd>{{ (e.gender === 'Male' ? 'employee.male' : 'employee.female') | t }}</dd>
              <dt>{{ 'employee.joiningDate' | t }}</dt><dd class="ltr">{{ e.joiningDate | d }}</dd>
              @if (e.terminationDate) {
                <dt>{{ 'settlement.lastDay' | t }}</dt><dd class="ltr">{{ e.terminationDate | d }}</dd>
              }
              <dt>{{ 'employee.qid' | t }}</dt><dd class="ltr">{{ e.qid || '—' }}</dd>
              <dt>{{ 'employee.passport' | t }}</dt><dd class="ltr">{{ e.passportNo || '—' }}</dd>
              <dt>{{ 'employee.iban' | t }}</dt><dd class="ltr">{{ e.bankShortName }} {{ e.iban || '—' }}</dd>
              <dt>{{ 'employee.inWps' | t }}</dt><dd>{{ (e.inWps ? 'app.yes' : 'app.no') | t }}</dd>
              <dt>{{ 'employee.socialInsurance' | t }}</dt><dd>{{ 'employee.scheme.' + e.socialInsuranceScheme | t }}</dd>
              <dt>{{ 'employee.mobile' | t }}</dt><dd class="ltr">{{ e.mobile || '—' }}</dd>
              <dt>{{ 'employee.email' | t }}</dt><dd class="ltr">{{ e.email || '—' }}</dd>
            </dl>
          </div>
        </mat-tab>
        <mat-tab [label]="'tab.contract' | t">
          <ng-template matTabContent><div class="tab"><app-contract-tab [employee]="e" /></div></ng-template>
        </mat-tab>
        <mat-tab [label]="'tab.documents' | t">
          <ng-template matTabContent><div class="tab"><app-documents-tab [employee]="e" /></div></ng-template>
        </mat-tab>
        <mat-tab [label]="'tab.leave' | t">
          <ng-template matTabContent><div class="tab"><app-leave-tab [employee]="e" /></div></ng-template>
        </mat-tab>
        @if (auth.can('hr.payroll:read')) {
          <mat-tab [label]="'tab.advances' | t">
            <ng-template matTabContent><div class="tab"><app-advances-tab [employee]="e" /></div></ng-template>
          </mat-tab>
          <mat-tab [label]="'tab.settlement' | t">
            <ng-template matTabContent><div class="tab"><app-settlement-tab [employee]="e" (changed)="reload()" /></div></ng-template>
          </mat-tab>
        }
      </mat-tab-group>
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: `
    .title { flex: 1; h1 { margin: 0; } }
    .tab { padding: 20px 4px; }
    .facts { display: grid; grid-template-columns: minmax(180px, 260px) 1fr; gap: 10px 24px; margin: 0; }
    dt { color: var(--mat-sys-on-surface-variant); }
    dd { margin: 0; }
  `
})
export class EmployeeDetailPage implements OnInit {
  readonly id = input.required<string>();
  readonly tab = input<string>();
  protected readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  private readonly i18n = inject(I18nService);

  protected readonly employee = signal<Employee | null>(null);
  protected readonly tabIndex = signal(0);

  ngOnInit(): void {
    const index = TABS.indexOf(this.tab() ?? '');
    if (index > 0) this.tabIndex.set(index);
    this.reload();
  }

  reload(): void {
    this.api.employee(this.id()).subscribe({ next: (e) => this.employee.set(e), error: (e) => this.notify.error(e) });
  }

  protected nationality(code: string): string {
    const n = NATIONALITIES.find((x) => x.code === code);
    return n ? (this.i18n.isArabic() ? n.ar : n.en) : code;
  }
}
