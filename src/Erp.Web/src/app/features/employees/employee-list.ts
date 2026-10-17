import { Component, inject, signal } from '@angular/core';
import { FormControl } from '@angular/forms';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { Router } from '@angular/router';
import { debounceTime, distinctUntilChanged } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { Employee } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { NATIONALITIES, SHARED } from '../../shared/shared';
import { I18nService } from '../../core/i18n/i18n.service';

@Component({
  selector: 'app-employee-list',
  imports: [...SHARED, MatPaginatorModule],
  template: `
    <div class="page-header">
      <h1>{{ 'employee.title' | t }}</h1>
      @if (auth.can('hr.employees:manage')) {
        <a mat-stroked-button routerLink="/employees/import"><mat-icon>upload_file</mat-icon> {{ 'import.title' | t }}</a>
        <a mat-flat-button routerLink="/employees/new"><mat-icon>person_add</mat-icon> {{ 'employee.new' | t }}</a>
      }
    </div>

    <div class="toolbar-row">
      <mat-form-field class="search">
        <mat-label>{{ 'app.search' | t }}</mat-label>
        <input matInput [formControl]="search" />
        <mat-icon matSuffix>search</mat-icon>
      </mat-form-field>
      <mat-form-field>
        <mat-label>{{ 'app.status' | t }}</mat-label>
        <mat-select [formControl]="status">
          <mat-option value="">{{ 'employee.allStatuses' | t }}</mat-option>
          <mat-option value="Active">{{ 'employee.status.Active' | t }}</mat-option>
          <mat-option value="Terminated">{{ 'employee.status.Terminated' | t }}</mat-option>
        </mat-select>
      </mat-form-field>
    </div>

    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    <div class="table-wrap">
      <table mat-table [dataSource]="rows()">
        <ng-container matColumnDef="empNo">
          <th mat-header-cell *matHeaderCellDef>{{ 'employee.empNo' | t }}</th>
          <td mat-cell *matCellDef="let e"><span class="ltr">{{ e.empNo }}</span></td>
        </ng-container>
        <ng-container matColumnDef="name">
          <th mat-header-cell *matHeaderCellDef>{{ 'employee.name' | t }}</th>
          <td mat-cell *matCellDef="let e">
            <div>{{ e | bi }}</div>
            <div class="secondary">{{ e | bi:'jobTitle' }}</div>
          </td>
        </ng-container>
        <ng-container matColumnDef="nationality">
          <th mat-header-cell *matHeaderCellDef>{{ 'employee.nationality' | t }}</th>
          <td mat-cell *matCellDef="let e">{{ nationality(e.nationality) }}</td>
        </ng-container>
        <ng-container matColumnDef="joining">
          <th mat-header-cell *matHeaderCellDef>{{ 'employee.joiningDate' | t }}</th>
          <td mat-cell *matCellDef="let e"><span class="ltr">{{ e.joiningDate | d }}</span></td>
        </ng-container>
        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef>{{ 'app.status' | t }}</th>
          <td mat-cell *matCellDef="let e">
            <span class="chip" [class.ok]="e.status === 'Active'">{{ 'employee.status.' + e.status | t }}</span>
          </td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns" class="clickable" (click)="open(row)"></tr>
      </table>
      @if (!loading() && rows().length === 0) {
        <div class="empty">{{ 'app.none' | t }}</div>
      }
      <mat-paginator [length]="total()" [pageSize]="pageSize" [pageSizeOptions]="[25, 50, 100]" (page)="page($event)" />
    </div>
  `,
  styles: `
    .search { flex: 1; max-width: 420px; }
    .secondary { font-size: 12px; color: var(--mat-sys-on-surface-variant); }
  `
})
export class EmployeeListPage {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly notify = inject(NotifyService);
  private readonly i18n = inject(I18nService);
  protected readonly auth = inject(AuthService);

  protected readonly columns = ['empNo', 'name', 'nationality', 'joining', 'status'];
  protected readonly rows = signal<Employee[]>([]);
  protected readonly total = signal(0);
  protected readonly loading = signal(false);
  protected readonly search = new FormControl('', { nonNullable: true });
  protected readonly status = new FormControl('Active', { nonNullable: true });
  protected pageSize = 50;
  private pageIndex = 0;

  constructor() {
    this.search.valueChanges.pipe(debounceTime(300), distinctUntilChanged(), takeUntilDestroyed()).subscribe(() => this.reload(0));
    this.status.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => this.reload(0));
    this.reload(0);
  }

  protected nationality(code: string): string {
    const n = NATIONALITIES.find((x) => x.code === code);
    return n ? (this.i18n.isArabic() ? n.ar : n.en) : code;
  }

  protected page(e: PageEvent): void {
    this.pageSize = e.pageSize;
    this.reload(e.pageIndex);
  }

  protected open(e: Employee): void {
    void this.router.navigate(['/employees', e.id]);
  }

  private reload(pageIndex: number): void {
    this.pageIndex = pageIndex;
    this.loading.set(true);
    this.api
      .employees({ search: this.search.value, status: this.status.value, page: this.pageIndex + 1, pageSize: this.pageSize })
      .subscribe({
        next: (r) => {
          this.rows.set(r.items);
          this.total.set(r.total);
          this.loading.set(false);
        },
        error: (e) => {
          this.loading.set(false);
          this.notify.error(e);
        }
      });
  }
}
