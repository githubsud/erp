import { Component, inject, signal } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { AppUser, Role } from '../../core/models';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

@Component({
  selector: 'app-users',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'users.title' | t }}</h1>
      @if (canManage && !showForm()) {
        <button mat-flat-button (click)="showForm.set(true)"><mat-icon>person_add</mat-icon> {{ 'users.new' | t }}</button>
      }
    </div>

    @if (showForm()) {
      <mat-card appearance="outlined" class="form-card">
        <form [formGroup]="form" (ngSubmit)="create()">
          <div class="form-grid">
            <mat-form-field>
              <mat-label>{{ 'login.email' | t }}</mat-label>
              <input matInput formControlName="email" dir="ltr" />
            </mat-form-field>
            <mat-form-field>
              <mat-label>{{ 'login.password' | t }}</mat-label>
              <input matInput type="password" formControlName="password" dir="ltr" autocomplete="new-password" />
            </mat-form-field>
            <mat-form-field>
              <mat-label>{{ 'users.displayNameAr' | t }}</mat-label>
              <input matInput formControlName="displayNameAr" dir="rtl" />
            </mat-form-field>
            <mat-form-field>
              <mat-label>{{ 'users.displayNameEn' | t }}</mat-label>
              <input matInput formControlName="displayNameEn" dir="ltr" />
            </mat-form-field>
            <mat-form-field>
              <mat-label>{{ 'users.language' | t }}</mat-label>
              <mat-select formControlName="preferredLanguage">
                <mat-option value="ar">العربية</mat-option>
                <mat-option value="en">English</mat-option>
              </mat-select>
            </mat-form-field>
            <mat-form-field>
              <mat-label>{{ 'users.roles' | t }}</mat-label>
              <mat-select formControlName="roles" multiple>
                @for (r of roles(); track r.code) { <mat-option [value]="r.code">{{ 'role.' + r.code | t }}</mat-option> }
              </mat-select>
            </mat-form-field>
          </div>
          <div class="actions">
            <button mat-button type="button" (click)="showForm.set(false)">{{ 'app.cancel' | t }}</button>
            <button mat-flat-button [disabled]="form.invalid">{{ 'app.create' | t }}</button>
          </div>
        </form>
      </mat-card>
    }

    <div class="table-wrap">
      <table mat-table [dataSource]="users()">
        <ng-container matColumnDef="name">
          <th mat-header-cell *matHeaderCellDef>{{ 'employee.name' | t }}</th>
          <td mat-cell *matCellDef="let u">{{ u | bi:'displayName' }}</td>
        </ng-container>
        <ng-container matColumnDef="email">
          <th mat-header-cell *matHeaderCellDef>{{ 'login.email' | t }}</th>
          <td mat-cell *matCellDef="let u" class="ltr">{{ u.email }}</td>
        </ng-container>
        <ng-container matColumnDef="roles">
          <th mat-header-cell *matHeaderCellDef>{{ 'users.roles' | t }}</th>
          <td mat-cell *matCellDef="let u">@for (r of u.roles; track r) { <span class="chip info">{{ 'role.' + r | t }}</span> }</td>
        </ng-container>
        <ng-container matColumnDef="lastLogin">
          <th mat-header-cell *matHeaderCellDef>{{ 'users.lastLogin' | t }}</th>
          <td mat-cell *matCellDef="let u" class="ltr">{{ u.lastLoginAt ? (u.lastLoginAt.slice(0, 10) | d) : '—' }}</td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="columns"></tr>
        <tr mat-row *matRowDef="let row; columns: columns"></tr>
      </table>
    </div>
  `,
  styles: `
    .form-card { padding: 16px 20px; margin-bottom: 16px; }
    .chip { margin-inline-end: 4px; }
  `
})
export class UsersPage {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly canManage = inject(AuthService).can('core.users:manage');
  protected readonly columns = ['name', 'email', 'roles', 'lastLogin'];
  protected readonly users = signal<AppUser[]>([]);
  protected readonly roles = signal<Role[]>([]);
  protected readonly showForm = signal(false);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required, Validators.minLength(10)]],
    displayNameEn: [''],
    displayNameAr: [''],
    preferredLanguage: ['ar'],
    roles: [[] as string[]]
  });

  constructor() {
    this.load();
    this.api.roles().subscribe((r) => this.roles.set(r));
  }

  create(): void {
    const v = this.form.getRawValue();
    this.api.createUser({ ...v, displayNameEn: v.displayNameEn || null, displayNameAr: v.displayNameAr || null }).subscribe({
      next: () => {
        this.notify.success();
        this.form.reset();
        this.showForm.set(false);
        this.load();
      },
      error: (e) => this.notify.error(e)
    });
  }

  private load(): void {
    this.api.users().subscribe({ next: (u) => this.users.set(u), error: (e) => this.notify.error(e) });
  }
}
