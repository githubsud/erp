import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { filter, switchMap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { addDaysIso, todayIso, weekStartIso } from '../../core/dates';
import { I18nService } from '../../core/i18n/i18n.service';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

interface WorkShift {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  startTime: string;
  endTime: string;
  breakMinutes: number;
  graceMinutes: number;
  color: string | null;
  isActive: boolean;
  hours: number;
}

interface RosterEmployee {
  id: string;
  empNo: string;
  nameEn: string;
  nameAr: string;
  departmentEn: string | null;
  departmentAr: string | null;
}

interface RosterCell {
  employeeId: string;
  workDate: string;
  shiftId: string | null;
  off: boolean;
}

interface RosterView {
  from: string;
  to: string;
  employees: RosterEmployee[];
  cells: RosterCell[];
  shifts: WorkShift[];
}

interface ReportRow {
  employeeId: string;
  empNo: string;
  nameEn: string;
  nameAr: string;
  rosteredDays: number;
  workedDays: number;
  onLeaveDays: number;
  absentDays: number;
  lateCount: number;
  lateMinutes: number;
  earlyCount: number;
  earlyMinutes: number;
  unrosteredDays: number;
  rosteredHours: number;
  workedHours: number;
}

interface RosterReport {
  rows: ReportRow[];
  exceptions: { employeeId: string; workDate: string; kind: 'Late' | 'Early' | 'Absent'; shiftCode: string | null; minutes: number }[];
}

/** Cell value in the grid: a shift id, 'off', or '' (nothing planned). */
type CellValue = string;
const OFF = 'off';
const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'] as const;

@Component({
  selector: 'app-rosters',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'roster.title' | t }}</h1>
      <span class="spacer"></span>
      @if (canManage) {
        <button mat-button (click)="manageShifts()"><mat-icon>schedule</mat-icon> {{ 'roster.shifts' | t }}</button>
      }
    </div>
    <mat-tab-group (selectedIndexChange)="tab.set($event)">
      <mat-tab [label]="'roster.plan' | t">
        <div class="toolbar">
          <button mat-icon-button (click)="move(-7)"><mat-icon class="flip-rtl">chevron_left</mat-icon></button>
          <span class="ltr week">{{ weekStart() | d }} – {{ weekEnd() | d }}</span>
          <button mat-icon-button (click)="move(7)"><mat-icon class="flip-rtl">chevron_right</mat-icon></button>
          <button mat-button (click)="goToday()">{{ 'roster.thisWeek' | t }}</button>
          <span class="spacer"></span>
          @if (canManage) {
            <button mat-stroked-button (click)="assign()"><mat-icon>event_repeat</mat-icon> {{ 'roster.assign' | t }}</button>
            <button mat-stroked-button (click)="copyPrevious()"><mat-icon>content_copy</mat-icon> {{ 'roster.copyPrevious' | t }}</button>
            <button mat-flat-button [disabled]="!dirty().size" (click)="save()">{{ 'app.save' | t }} @if (dirty().size) { ({{ dirty().size }}) }</button>
          }
        </div>
        <div class="table-wrap">
          <table class="line-editor grid">
            <thead>
              <tr>
                <th>{{ 'roster.employee' | t }}</th>
                @for (d of dates(); track d; let i = $index) {
                  <th [class.today]="d === today">{{ 'roster.day.' + days[i] | t }}<br /><span class="ltr secondary">{{ d.slice(8, 10) }}/{{ d.slice(5, 7) }}</span></th>
                }
                <th class="num">{{ 'roster.hours' | t }}</th>
              </tr>
            </thead>
            <tbody>
              @for (e of view()?.employees ?? []; track e.id) {
                <tr>
                  <td><span class="ltr secondary">{{ e.empNo }}</span> {{ e | bi }}</td>
                  @for (d of dates(); track d) {
                    <td class="cell" [style.background]="color(e.id, d)">
                      @if (canManage) {
                        <select [value]="value(e.id, d)" (change)="set(e.id, d, $any($event.target).value)">
                          <option value=""></option>
                          @for (s of activeShifts(); track s.id) { <option [value]="s.id">{{ s.code }}</option> }
                          <option [value]="OFF">{{ 'roster.off' | t }}</option>
                        </select>
                      } @else {
                        {{ label(e.id, d) }}
                      }
                    </td>
                  }
                  <td class="num ltr">{{ hours(e.id) }}</td>
                </tr>
              }
            </tbody>
          </table>
          @if (view() && !view()!.employees.length) { <p class="secondary pad">{{ 'app.none' | t }}</p> }
        </div>
        <div class="legend">
          @for (s of activeShifts(); track s.id) {
            <span class="chip" [style.background]="s.color || null"><b class="ltr">{{ s.code }}</b> {{ s | bi }} <span class="ltr">{{ s.startTime.slice(0, 5) }}–{{ s.endTime.slice(0, 5) }}</span></span>
          }
        </div>
      </mat-tab>
      <mat-tab [label]="'roster.report' | t">
        <div class="toolbar">
          <mat-form-field appearance="outline" class="date"><mat-label>{{ 'app.from' | t }}</mat-label><input matInput type="date" [formControl]="from" /></mat-form-field>
          <mat-form-field appearance="outline" class="date"><mat-label>{{ 'app.to' | t }}</mat-label><input matInput type="date" [formControl]="to" /></mat-form-field>
          <button mat-flat-button (click)="loadReport()">{{ 'roster.run' | t }}</button>
        </div>
        @if (report(); as r) {
          <div class="table-wrap">
            <table class="line-editor">
              <thead><tr><th>{{ 'roster.employee' | t }}</th><th class="num">{{ 'roster.rostered' | t }}</th><th class="num">{{ 'roster.worked' | t }}</th>
                <th class="num">{{ 'roster.onLeave' | t }}</th><th class="num">{{ 'roster.absent' | t }}</th><th class="num">{{ 'roster.late' | t }}</th>
                <th class="num">{{ 'roster.early' | t }}</th><th class="num">{{ 'roster.unrostered' | t }}</th><th class="num">{{ 'roster.hours' | t }}</th></tr></thead>
              <tbody>
                @for (x of r.rows; track x.employeeId) {
                  <tr>
                    <td><span class="ltr secondary">{{ x.empNo }}</span> {{ x | bi }}</td>
                    <td class="num ltr">{{ x.rosteredDays }}</td>
                    <td class="num ltr">{{ x.workedDays }}</td>
                    <td class="num ltr">{{ x.onLeaveDays }}</td>
                    <td class="num ltr" [class.bad]="x.absentDays">{{ x.absentDays }}</td>
                    <td class="num ltr" [class.bad]="x.lateCount">{{ x.lateCount }} @if (x.lateMinutes) { <small>({{ x.lateMinutes }}m)</small> }</td>
                    <td class="num ltr" [class.bad]="x.earlyCount">{{ x.earlyCount }} @if (x.earlyMinutes) { <small>({{ x.earlyMinutes }}m)</small> }</td>
                    <td class="num ltr">{{ x.unrosteredDays }}</td>
                    <td class="num ltr">{{ x.workedHours }} / {{ x.rosteredHours }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          @if (r.exceptions.length) {
            <h3>{{ 'roster.exceptions' | t }}</h3>
            <div class="table-wrap">
              <table class="line-editor">
                <thead><tr><th>{{ 'app.date' | t }}</th><th>{{ 'roster.employee' | t }}</th><th>{{ 'roster.shift' | t }}</th><th>{{ 'roster.issue' | t }}</th></tr></thead>
                <tbody>
                  @for (x of r.exceptions; track $index) {
                    <tr>
                      <td class="ltr">{{ x.workDate | d }}</td>
                      <td>{{ employeeName(x.employeeId) }}</td>
                      <td class="ltr">{{ x.shiftCode }}</td>
                      <td>{{ 'roster.kind.' + x.kind | t }} @if (x.minutes) { <span class="ltr">{{ x.minutes }}m</span> }</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
          <p class="secondary">{{ 'roster.reportHint' | t }}</p>
        }
      </mat-tab>
    </mat-tab-group>
  `,
  styles: `
    .spacer { flex: 1; }
    .secondary { color: var(--mat-sys-on-surface-variant); }
    .pad { padding: 16px; }
    .toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin-block: 12px; }
    .week { font-weight: 600; }
    .grid th { text-align: center; white-space: nowrap; }
    .grid th.today { color: var(--mat-sys-primary); }
    .cell { text-align: center; padding: 2px; }
    .cell select { width: 100%; min-width: 64px; border: none; background: transparent; font: inherit; color: inherit; padding: 6px 2px; }
    .legend { display: flex; flex-wrap: wrap; gap: 8px; margin-block: 12px; }
    .date { width: 170px; }
    .bad { color: var(--mat-sys-error); font-weight: 600; }
  `
})
export class RostersPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  private readonly i18n = inject(I18nService);
  protected readonly canManage = inject(AuthService).can('hr.rosters:manage');
  protected readonly OFF = OFF;
  protected readonly days = DAYS;
  protected readonly today = todayIso();
  protected readonly tab = signal(0);
  protected readonly weekStart = signal(weekStartIso(todayIso()));
  protected readonly weekEnd = computed(() => addDaysIso(this.weekStart(), 6));
  protected readonly dates = computed(() => DAYS.map((_, i) => addDaysIso(this.weekStart(), i)));
  protected readonly view = signal<RosterView | null>(null);
  protected readonly activeShifts = computed(() => (this.view()?.shifts ?? []).filter((s) => s.isActive));
  private readonly cells = signal(new Map<string, CellValue>());
  protected readonly dirty = signal(new Map<string, CellValue>());
  protected readonly report = signal<RosterReport | null>(null);
  protected readonly from = new FormControl(addDaysIso(weekStartIso(todayIso()), -7), { nonNullable: true });
  protected readonly to = new FormControl(addDaysIso(weekStartIso(todayIso()), -1), { nonNullable: true });
  private employees = new Map<string, RosterEmployee>();

  ngOnInit(): void {
    this.load();
  }

  private key = (employeeId: string, date: string) => `${employeeId}|${date}`;

  protected load(): void {
    this.api.get<RosterView>('hr/rosters', { from: this.weekStart(), to: this.weekEnd() }).subscribe({
      next: (v) => {
        this.view.set(v);
        this.employees = new Map(v.employees.map((e) => [e.id, e]));
        this.cells.set(new Map(v.cells.map((c) => [this.key(c.employeeId, c.workDate), c.off ? OFF : (c.shiftId ?? '')])));
        this.dirty.set(new Map());
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected value(employeeId: string, date: string): CellValue {
    const k = this.key(employeeId, date);
    return this.dirty().get(k) ?? this.cells().get(k) ?? '';
  }

  private shift(id: CellValue): WorkShift | undefined {
    return this.view()?.shifts.find((s) => s.id === id);
  }

  protected label(employeeId: string, date: string): string {
    const v = this.value(employeeId, date);
    return v === OFF ? this.i18n.t('roster.off') : (this.shift(v)?.code ?? '');
  }

  protected color(employeeId: string, date: string): string | null {
    const v = this.value(employeeId, date);
    if (v === OFF) return 'var(--mat-sys-surface-container-high)';
    return this.shift(v)?.color ?? null;
  }

  protected hours(employeeId: string): number {
    return this.dates().reduce((sum, d) => sum + (this.shift(this.value(employeeId, d))?.hours ?? 0), 0);
  }

  protected employeeName(id: string): string {
    const e = this.employees.get(id) ?? this.report()?.rows.find((r) => r.employeeId === id);
    return e ? `${e.empNo} ${this.i18n.isArabic() ? e.nameAr : e.nameEn}` : '';
  }

  protected set(employeeId: string, date: string, v: CellValue): void {
    const k = this.key(employeeId, date);
    this.dirty.update((m) => {
      const next = new Map(m);
      if ((this.cells().get(k) ?? '') === v) next.delete(k);
      else next.set(k, v);
      return next;
    });
  }

  protected move(days: number): void {
    this.goTo(addDaysIso(this.weekStart(), days));
  }

  protected goToday(): void {
    this.goTo(weekStartIso(todayIso()));
  }

  private goTo(weekStart: string): void {
    if (this.dirty().size && !confirm(this.i18n.t('roster.discardChanges'))) return;
    this.weekStart.set(weekStart);
    this.load();
  }

  protected save(): void {
    const body = [...this.dirty().entries()].map(([k, v]) => {
      const [employeeId, workDate] = k.split('|');
      return { employeeId, workDate, shiftId: v && v !== OFF ? v : null, off: v === OFF };
    });
    this.api.put('hr/rosters', body).subscribe({
      next: () => {
        this.notify.success();
        this.load();
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected copyPrevious(): void {
    if (!confirm(this.i18n.t('roster.copyConfirm'))) return;
    this.api.post('hr/rosters/copy-week', { sourceWeekStart: addDaysIso(this.weekStart(), -7), targetWeekStart: this.weekStart() }).subscribe({
      next: () => {
        this.notify.success();
        this.load();
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected assign(): void {
    const v = this.view();
    if (!v) return;
    this.dialog
      .open(AssignDialog, { data: { employees: v.employees, shifts: this.activeShifts(), from: this.weekStart(), to: this.weekEnd() }, width: '520px' })
      .afterClosed()
      .pipe(
        filter((body) => !!body),
        switchMap((body) => this.api.post('hr/rosters/assign', body))
      )
      .subscribe({
        next: () => {
          this.notify.success();
          this.load();
        },
        error: (e) => this.notify.error(e)
      });
  }

  protected manageShifts(): void {
    this.dialog
      .open(ShiftsDialog, { data: this.view()?.shifts ?? [], width: '720px' })
      .afterClosed()
      .subscribe(() => this.load());
  }

  protected loadReport(): void {
    this.api.get<RosterReport>('hr/rosters/report', { from: this.from.value, to: this.to.value }).subscribe({
      next: (r) => this.report.set(r),
      error: (e) => this.notify.error(e)
    });
  }
}

@Component({
  selector: 'app-roster-assign-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'roster.assign' | t }}</h2>
    <mat-dialog-content [formGroup]="form" class="form">
      <mat-form-field appearance="outline"><mat-label>{{ 'roster.employees' | t }}</mat-label>
        <mat-select formControlName="employeeIds" multiple>
          @for (e of data.employees; track e.id) { <mat-option [value]="e.id"><span class="ltr">{{ e.empNo }}</span> {{ e | bi }}</mat-option> }
        </mat-select>
      </mat-form-field>
      <button mat-button type="button" (click)="all()">{{ 'roster.selectAll' | t }}</button>
      <mat-form-field appearance="outline"><mat-label>{{ 'roster.shift' | t }}</mat-label>
        <mat-select formControlName="shiftId">@for (s of data.shifts; track s.id) { <mat-option [value]="s.id">{{ s.code }} · {{ s | bi }}</mat-option> }</mat-select>
      </mat-form-field>
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>{{ 'app.from' | t }}</mat-label><input matInput type="date" formControlName="from" /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>{{ 'app.to' | t }}</mat-label><input matInput type="date" formControlName="to" /></mat-form-field>
      </div>
      <mat-form-field appearance="outline"><mat-label>{{ 'roster.restDays' | t }}</mat-label>
        <mat-select formControlName="restDays" multiple>@for (d of days; track d) { <mat-option [value]="d">{{ 'roster.day.' + d | t }}</mat-option> }</mat-select>
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid" (click)="ref.close(form.getRawValue())">{{ 'roster.assign' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `.form { display: grid; gap: 4px; } .row { display: flex; gap: 8px; } .row mat-form-field { flex: 1; }`
})
class AssignDialog {
  protected readonly ref = inject(MatDialogRef<AssignDialog>);
  protected readonly data = inject<{ employees: RosterEmployee[]; shifts: WorkShift[]; from: string; to: string }>(MAT_DIALOG_DATA);
  protected readonly days = DAYS;
  protected readonly form = inject(FormBuilder).nonNullable.group({
    employeeIds: [[] as string[], Validators.required],
    shiftId: ['', Validators.required],
    from: [this.data.from, Validators.required],
    to: [this.data.to, Validators.required],
    restDays: [['Friday'] as string[]]
  });

  protected all(): void {
    this.form.controls.employeeIds.setValue(this.data.employees.map((e) => e.id));
  }
}

@Component({
  selector: 'app-shifts-dialog',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'roster.shifts' | t }}</h2>
    <mat-dialog-content>
      <table class="line-editor">
        <thead><tr><th>{{ 'roster.code' | t }}</th><th>{{ 'roster.name' | t }}</th><th>{{ 'roster.time' | t }}</th><th class="num">{{ 'roster.hours' | t }}</th><th></th></tr></thead>
        <tbody>
          @for (s of shifts(); track s.id) {
            <tr [class.inactive]="!s.isActive">
              <td class="ltr"><span class="swatch" [style.background]="s.color"></span> {{ s.code }}</td>
              <td>{{ s | bi }}</td>
              <td class="ltr">{{ s.startTime.slice(0, 5) }}–{{ s.endTime.slice(0, 5) }}</td>
              <td class="num ltr">{{ s.hours }}</td>
              <td><button mat-button (click)="edit(s)">{{ 'app.edit' | t }}</button></td>
            </tr>
          }
        </tbody>
      </table>
      <h3>{{ (editing() ? 'roster.editShift' : 'roster.newShift') | t }}</h3>
      <div [formGroup]="form" class="form">
        <mat-form-field appearance="outline"><mat-label>{{ 'roster.code' | t }}</mat-label><input matInput class="ltr" formControlName="code" /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>{{ 'roster.nameEn' | t }}</mat-label><input matInput formControlName="nameEn" /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>{{ 'roster.nameAr' | t }}</mat-label><input matInput formControlName="nameAr" /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>{{ 'roster.start' | t }}</mat-label><input matInput type="time" class="ltr" formControlName="startTime" /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>{{ 'roster.end' | t }}</mat-label><input matInput type="time" class="ltr" formControlName="endTime" /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>{{ 'roster.break' | t }}</mat-label><input matInput type="number" class="ltr" formControlName="breakMinutes" /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>{{ 'roster.grace' | t }}</mat-label><input matInput type="number" class="ltr" formControlName="graceMinutes" /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>{{ 'roster.color' | t }}</mat-label><input matInput type="color" formControlName="color" /></mat-form-field>
        <mat-slide-toggle formControlName="isActive">{{ 'roster.active' | t }}</mat-slide-toggle>
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      @if (editing()) { <button mat-button (click)="reset()">{{ 'roster.newShift' | t }}</button> }
      <button mat-button mat-dialog-close>{{ 'app.close' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid" (click)="save()">{{ 'app.save' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `
    .form { display: grid; grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); gap: 4px 8px; align-items: center; }
    .swatch { display: inline-block; width: 12px; height: 12px; border-radius: 3px; vertical-align: middle; }
    .inactive { opacity: 0.5; }
  `
})
class ShiftsDialog {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly shifts = signal<WorkShift[]>(inject<WorkShift[]>(MAT_DIALOG_DATA));
  protected readonly editing = signal<WorkShift | null>(null);
  private readonly fb = inject(FormBuilder).nonNullable;
  protected readonly form = this.fb.group({
    code: ['', Validators.required],
    nameEn: ['', Validators.required],
    nameAr: ['', Validators.required],
    startTime: ['07:00', Validators.required],
    endTime: ['15:00', Validators.required],
    breakMinutes: [0, Validators.min(0)],
    graceMinutes: [10, Validators.min(0)],
    color: ['#cfe8ff'],
    isActive: [true]
  });

  protected edit(s: WorkShift): void {
    this.editing.set(s);
    this.form.reset({
      code: s.code, nameEn: s.nameEn, nameAr: s.nameAr, startTime: s.startTime.slice(0, 5), endTime: s.endTime.slice(0, 5), breakMinutes: s.breakMinutes,
      graceMinutes: s.graceMinutes, color: s.color ?? '#cfe8ff', isActive: s.isActive
    });
    this.form.controls.code.disable();
  }

  protected reset(): void {
    this.editing.set(null);
    this.form.reset();
    this.form.controls.code.enable();
  }

  protected save(): void {
    const v = this.form.getRawValue();
    const body = { ...v, startTime: `${v.startTime}:00`.slice(0, 8), endTime: `${v.endTime}:00`.slice(0, 8) };
    const e = this.editing();
    (e ? this.api.put<WorkShift>(`hr/shifts/${e.id}`, body) : this.api.post<WorkShift>('hr/shifts', body)).subscribe({
      next: (saved) => {
        this.shifts.update((list) => (e ? list.map((x) => (x.id === saved.id ? saved : x)) : [...list, saved]));
        this.notify.success();
        this.reset();
      },
      error: (err) => this.notify.error(err)
    });
  }
}
