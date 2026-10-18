import { Component, inject, OnInit, signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { filter, switchMap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { NotifyService } from '../../core/notify.service';
import { ConfirmDialog } from '../../shared/dialogs';
import { SHARED } from '../../shared/shared';

interface FiscalPeriod {
  id: string;
  periodNo: number;
  startDate: string;
  endDate: string;
  status: 'Open' | 'Closed';
}

interface FiscalYear {
  id: string;
  name: string;
  startDate: string;
  endDate: string;
  status: 'Open' | 'Closed';
  periods: FiscalPeriod[];
}

interface ChecklistItem {
  code: string;
  textEn: string;
  textAr: string;
  ok: boolean;
  blocking: boolean;
  detail: string | null;
}

interface Checklist {
  fiscalYearId: string;
  items: ChecklistItem[];
  canClose: boolean;
}

/** Fiscal years and periods: close a month, or run the year-end checklist and lock the year. */
@Component({
  selector: 'app-fiscal-years',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'fy.title' | t }}</h1></div>
    <p class="secondary">{{ 'fy.hint' | t }}</p>
    @for (y of years(); track y.id) {
      <mat-card appearance="outlined" class="year">
        <div class="head">
          <h2>{{ y.name }} <span class="secondary ltr">{{ y.startDate | d }} – {{ y.endDate | d }}</span></h2>
          <span class="chip" [class.ok]="y.status === 'Open'">{{ 'fy.status.' + y.status | t }}</span>
          <span class="spacer"></span>
          @if (canManage) {
            @if (y.status === 'Open') {
              <button mat-stroked-button (click)="check(y)"><mat-icon>checklist</mat-icon> {{ 'fy.closeYear' | t }}</button>
            } @else {
              <button mat-button (click)="reopen(y)">{{ 'fy.reopen' | t }}</button>
            }
          }
        </div>
        @if (checklist()?.fiscalYearId === y.id) {
          <div class="checklist">
            @for (i of checklist()!.items; track i.code) {
              <div class="item" [class.bad]="!i.ok && i.blocking">
                <mat-icon>{{ i.ok ? 'check_circle' : i.blocking ? 'cancel' : 'warning' }}</mat-icon>
                <span>{{ i | bi:'text' }}</span>
                @if (i.detail) { <span class="secondary">— {{ i.detail }}</span> }
              </div>
            }
            <div class="actions">
              <button mat-button (click)="checklist.set(null)">{{ 'app.cancel' | t }}</button>
              <button mat-flat-button [disabled]="!checklist()!.canClose" (click)="close(y)"><mat-icon>lock</mat-icon> {{ 'fy.closeNow' | t }}</button>
            </div>
          </div>
        }
        <div class="periods">
          @for (p of y.periods; track p.id) {
            <button class="period" [class.closed]="p.status === 'Closed'" [disabled]="!canManage || y.status === 'Closed'" (click)="toggle(p)"
              [matTooltip]="(p.status === 'Open' ? 'fy.closePeriod' : 'fy.reopenPeriod') | t">
              <span>{{ 'month.' + month(p) | t }}</span>
              <mat-icon>{{ p.status === 'Open' ? 'lock_open' : 'lock' }}</mat-icon>
            </button>
          }
        </div>
      </mat-card>
    }
  `,
  styles: `
    .year { padding: 16px; margin-top: 16px; }
    .head { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
    h2 { margin: 0; font: var(--mat-sys-title-large); }
    .spacer { flex: 1; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 13px; font-weight: 400; }
    .periods { display: grid; grid-template-columns: repeat(auto-fill, minmax(110px, 1fr)); gap: 8px; margin-top: 12px; }
    .period { display: flex; justify-content: space-between; align-items: center; gap: 6px; padding: 8px 10px; border-radius: 10px; cursor: pointer;
      border: 1px solid var(--mat-sys-outline-variant); background: var(--mat-sys-surface); color: inherit; font: inherit; }
    .period.closed { background: var(--mat-sys-surface-container-high); color: var(--mat-sys-on-surface-variant); }
    .period:disabled { cursor: default; }
    .checklist { margin-top: 12px; padding: 12px; border-radius: 12px; background: var(--mat-sys-surface-container); }
    .item { display: flex; align-items: center; gap: 8px; padding: 4px 0; }
    .item mat-icon { color: #0b5a35; }
    .item.bad mat-icon { color: #b3261e; }
    .actions { display: flex; justify-content: flex-end; gap: 8px; margin-top: 8px; }
  `
})
export class FiscalYearsPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  private readonly dialog = inject(MatDialog);
  protected readonly canManage = inject(AuthService).can('fin.periods:manage');
  protected readonly years = signal<FiscalYear[]>([]);
  protected readonly checklist = signal<Checklist | null>(null);

  ngOnInit(): void {
    this.load();
  }

  protected month(p: FiscalPeriod): number {
    return Number(p.startDate.slice(5, 7));
  }

  protected check(y: FiscalYear): void {
    this.api.get<Checklist>(`finance/fiscal-years/${y.id}/close-checklist`).subscribe({ next: (c) => this.checklist.set(c), error: (e) => this.notify.error(e) });
  }

  protected close(y: FiscalYear): void {
    this.dialog
      .open(ConfirmDialog, { data: { title: 'fy.closeYear', message: 'fy.closeConfirm' } })
      .afterClosed()
      .pipe(filter(Boolean), switchMap(() => this.api.post(`finance/fiscal-years/${y.id}/close`)))
      .subscribe({
        next: () => {
          this.checklist.set(null);
          this.notify.success('fy.closed');
          this.load();
        },
        error: (e) => this.notify.error(e)
      });
  }

  protected reopen(y: FiscalYear): void {
    this.dialog
      .open(ConfirmDialog, { data: { title: 'fy.reopen', message: 'fy.reopenConfirm' } })
      .afterClosed()
      .pipe(filter(Boolean), switchMap(() => this.api.post(`finance/fiscal-years/${y.id}/reopen`)))
      .subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  protected toggle(p: FiscalPeriod): void {
    this.api.post(`finance/periods/${p.id}/${p.status === 'Open' ? 'close' : 'reopen'}`).subscribe({ next: () => this.load(), error: (e) => this.notify.error(e) });
  }

  private load(): void {
    this.api.get<FiscalYear[]>('finance/fiscal-years').subscribe({
      next: (y) => this.years.set([...y].sort((a, b) => b.startDate.localeCompare(a.startDate))),
      error: (e) => this.notify.error(e)
    });
  }
}
