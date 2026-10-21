import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { Router } from '@angular/router';
import { searchHelp } from '../../core/help-articles';
import { I18nService } from '../../core/i18n/i18n.service';
import { NotifyService } from '../../core/notify.service';
import {
  SUPPORT_FILE_ACCEPT, SupportService, TICKET_PRIORITIES, TICKET_TYPES, TYPE_ICON, TicketDetail, TicketRow, TicketType, checkSupportFiles
} from '../../core/support.service';
import { SHARED } from '../../shared/shared';
import { TicketThread, dateTime } from '../../shared/ticket-thread';

/** Status chip colours shared by the support pages. */
export const STATUS_CHIP: Record<string, string> = { New: 'warn', InProgress: '', WaitingCustomer: 'warn', Solved: 'ok', Closed: '' };

/** A new request to the vendor. The page it was opened from is attached so support knows where it happened. */
@Component({
  selector: 'app-new-request',
  imports: [...SHARED],
  template: `
    <h2 mat-dialog-title>{{ 'help.new' | t }}</h2>
    <mat-dialog-content [formGroup]="form">
      <p class="secondary">{{ 'help.newHint' | t }}</p>
      <div class="types">
        @for (ty of types; track ty) {
          <button type="button" mat-stroked-button [class.selected]="form.controls.type.value === ty" (click)="form.controls.type.setValue(ty)">
            <mat-icon>{{ icon[ty] }}</mat-icon> {{ 'help.type.' + ty | t }}
          </button>
        }
      </div>
      <mat-form-field class="full"><mat-label>{{ 'help.subject' | t }}</mat-label><input matInput formControlName="subject" maxlength="200" dir="auto" /></mat-form-field>
      @if (suggested().length) {
        <div class="suggest">
          <span class="secondary small">{{ 'help.maybe' | t }}</span>
          @for (a of suggested(); track a.id) {
            <a mat-stroked-button [routerLink]="['/help', a.id]" target="_blank"><mat-icon>{{ a.icon }}</mat-icon> {{ a | bi:'title' }}</a>
          }
        </div>
      }
      <mat-form-field class="full"><mat-label>{{ 'help.body' | t }}</mat-label>
        <textarea matInput formControlName="body" rows="6" dir="auto" [placeholder]="'help.bodyPlaceholder.' + form.controls.type.value | t"></textarea></mat-form-field>
      <mat-form-field><mat-label>{{ 'help.priority' | t }}</mat-label>
        <mat-select formControlName="priority">@for (p of priorities; track p) { <mat-option [value]="p">{{ 'help.prio.' + p | t }}</mat-option> }</mat-select>
      </mat-form-field>
      <div class="attach">
        <button type="button" mat-stroked-button (click)="picker.click()"><mat-icon>attach_file</mat-icon> {{ 'help.attach' | t }}</button>
        <input #picker type="file" multiple hidden [accept]="accept" (change)="pick(picker)" />
        @for (f of files(); track f.name) {
          <span class="chip">{{ f.name }} <button type="button" class="x" (click)="remove(f)" [attr.aria-label]="'app.delete' | t">×</button></span>
        }
      </div>
      <p class="secondary small">{{ 'help.attachHint' | t }}</p>
      @if (data.page) { <p class="secondary small">{{ 'help.pageNote' | t }} <span class="ltr">{{ data.page }}</span></p> }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ 'app.cancel' | t }}</button>
      <button mat-flat-button [disabled]="form.invalid || busy()" (click)="send()"><mat-icon class="flip-rtl">send</mat-icon> {{ 'help.send' | t }}</button>
    </mat-dialog-actions>
  `,
  styles: `
    .secondary { color: var(--mat-sys-on-surface-variant); }
    .small { font-size: 12px; margin: 4px 0; }
    .types { display: grid; grid-template-columns: repeat(auto-fill, minmax(150px, 1fr)); gap: 8px; margin-block-end: 16px; }
    .types .selected { background: var(--mat-sys-primary-container); border-color: var(--mat-sys-primary); }
    .full { width: 100%; }
    .attach { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
    .x { border: none; background: none; cursor: pointer; font-size: 16px; padding: 0 2px; }
    .suggest { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; margin: -8px 0 12px; }
  `
})
export class NewRequestDialog {
  protected readonly data = inject<{ page: string | null; type?: TicketType }>(MAT_DIALOG_DATA);
  private readonly support = inject(SupportService);
  private readonly i18n = inject(I18nService);
  private readonly notify = inject(NotifyService);
  private readonly ref = inject(MatDialogRef<NewRequestDialog>);
  protected readonly types = TICKET_TYPES;
  protected readonly priorities = TICKET_PRIORITIES;
  protected readonly icon = TYPE_ICON;
  protected readonly accept = SUPPORT_FILE_ACCEPT;
  protected readonly files = signal<File[]>([]);
  protected readonly busy = signal(false);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    type: [this.data.type ?? ('Problem' as TicketType)],
    priority: ['Normal' as string],
    subject: ['', [Validators.required, Validators.maxLength(200)]],
    body: ['', Validators.required]
  });

  private readonly subjectText = toSignal(this.form.controls.subject.valueChanges, { initialValue: '' });
  /** Articles that may already answer it, from the words of the subject. */
  protected readonly suggested = computed(() => searchHelp(this.subjectText()));

  protected pick(input: HTMLInputElement): void {
    const next = [...this.files(), ...Array.from(input.files ?? [])];
    input.value = '';
    const problem = checkSupportFiles(next);
    if (problem) {
      this.notify.info(this.i18n.t(problem.key, { name: problem.name ?? '' }));
      return;
    }
    this.files.set(next);
  }

  protected remove(f: File): void {
    this.files.update((list) => list.filter((x) => x !== f));
  }

  protected send(): void {
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.support.create({
      type: v.type, priority: v.priority as TicketDetail['ticket']['priority'], subject: v.subject.trim(), body: v.body.trim(), language: this.i18n.lang(),
      page: this.data.page, files: this.files()
    }).subscribe({
      next: (t) => {
        this.notify.info(this.i18n.t('help.sent', { no: t.ticket.ticketNo }));
        this.ref.close(t);
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }
}

/** Opens the new-request dialog and goes to the request once it is sent. */
export function openNewRequest(dialog: MatDialog, router: Router, type?: TicketType): void {
  const page = router.url.startsWith('/support') ? null : router.url;
  dialog.open(NewRequestDialog, { data: { page, type }, width: '640px', maxWidth: '96vw', autoFocus: false })
    .afterClosed().subscribe((t?: TicketDetail) => {
      if (t) void router.navigate(['/support', t.ticket.id]);
    });
}

@Component({
  selector: 'app-support-list',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1>{{ 'help.title' | t }}</h1>
      <span class="spacer"></span>
      <button mat-flat-button (click)="create()"><mat-icon>add</mat-icon> {{ 'help.new' | t }}</button>
    </div>
    <p class="secondary">{{ 'help.intro' | t }}</p>
    <div class="quick">
      @for (ty of types; track ty) {
        <button mat-stroked-button (click)="create(ty)"><mat-icon>{{ icon[ty] }}</mat-icon> {{ 'help.type.' + ty | t }}</button>
      }
    </div>
    @if (loading()) { <mat-progress-bar mode="indeterminate" /> }
    <div class="list">
      @for (r of rows(); track r.id) {
        <a class="row" [routerLink]="['/support', r.id]" [class.unread]="r.unread">
          <mat-icon class="type">{{ icon[r.type] }}</mat-icon>
          <div class="main">
            <div class="subject" dir="auto">{{ r.subject }}</div>
            <div class="secondary small"><span class="ltr">{{ r.ticketNo }}</span> · {{ 'help.type.' + r.type | t }} · {{ r.requesterName }} · <span class="ltr">{{ when(r.lastActivityAt) }}</span></div>
          </div>
          @if (r.unread) { <span class="chip ok">{{ 'help.newAnswer' | t }}</span> }
          <span [class]="'chip ' + chip[r.status]">{{ 'help.status.' + r.status | t }}</span>
        </a>
      }
    </div>
    @if (!loading() && !rows().length) {
      <mat-card appearance="outlined" class="empty"><mat-icon>support_agent</mat-icon><p>{{ 'help.empty' | t }}</p></mat-card>
    }
  `,
  styles: `
    .spacer { flex: 1; }
    .secondary { color: var(--mat-sys-on-surface-variant); }
    .small { font-size: 12px; }
    .quick { display: flex; flex-wrap: wrap; gap: 8px; margin-block: 8px 16px; }
    .list { display: flex; flex-direction: column; gap: 8px; }
    .row { display: flex; gap: 12px; align-items: center; padding: 12px 14px; border: 1px solid var(--mat-sys-outline-variant); border-radius: 12px;
      color: inherit; text-decoration: none; background: var(--mat-sys-surface); }
    .row.unread { border-color: var(--mat-sys-primary); }
    .row.unread .subject { font-weight: 600; }
    .type { color: var(--mat-sys-primary); flex: none; }
    .main { flex: 1; min-width: 0; }
    .subject { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .empty { display: flex; flex-direction: column; align-items: center; padding: 32px; text-align: center; color: var(--mat-sys-on-surface-variant); }
  `
})
export class SupportListPage implements OnInit {
  private readonly support = inject(SupportService);
  private readonly notify = inject(NotifyService);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  protected readonly rows = signal<TicketRow[]>([]);
  protected readonly loading = signal(true);
  protected readonly types = TICKET_TYPES;
  protected readonly icon = TYPE_ICON;
  protected readonly chip = STATUS_CHIP;
  protected readonly when = dateTime;

  ngOnInit(): void {
    this.support.tickets().subscribe({
      next: (r) => {
        this.rows.set(r);
        this.loading.set(false);
      },
      error: (e) => {
        this.loading.set(false);
        this.notify.error(e);
      }
    });
  }

  protected create(type?: TicketType): void {
    openNewRequest(this.dialog, this.router, type);
  }
}

@Component({
  selector: 'app-support-ticket',
  imports: [...SHARED, TicketThread],
  template: `
    @if (t(); as d) {
      <div class="page-header">
        <a mat-icon-button routerLink="/support" [attr.aria-label]="'app.back' | t"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
        <h1 dir="auto">{{ d.ticket.subject }}</h1>
      </div>
      <div class="facts">
        <span class="ltr">{{ d.ticket.ticketNo }}</span>
        <span class="chip">{{ 'help.type.' + d.ticket.type | t }}</span>
        <span [class]="'chip ' + chip[d.ticket.status]">{{ 'help.status.' + d.ticket.status | t }}</span>
        <span class="secondary">{{ 'help.openedBy' | t: { name: d.ticket.requesterName } }} · <span class="ltr">{{ when(d.ticket.createdAt) }}</span></span>
      </div>
      @if (d.ticket.status === 'WaitingCustomer') { <p class="banner">{{ 'help.waitingYou' | t }}</p> }
      <app-ticket-thread [messages]="d.messages" mine="Customer" (openFile)="support.open($event)" />

      @if (d.ticket.status !== 'Closed') {
        <mat-card appearance="outlined" class="reply">
          <mat-form-field class="full"><mat-label>{{ 'help.reply' | t }}</mat-label>
            <textarea matInput [formControl]="text" rows="3" dir="auto"></textarea></mat-form-field>
          <div class="actions">
            <button mat-stroked-button (click)="picker.click()"><mat-icon>attach_file</mat-icon> {{ 'help.attach' | t }}</button>
            <input #picker type="file" multiple hidden [accept]="accept" (change)="pick(picker)" />
            @for (f of files(); track f.name) { <span class="chip">{{ f.name }}</span> }
            <span class="spacer"></span>
            @if (d.ticket.status !== 'Solved') {
              <button mat-button (click)="solve()"><mat-icon>task_alt</mat-icon> {{ 'help.markSolved' | t }}</button>
            }
            <button mat-flat-button [disabled]="busy() || (!text.value.trim() && !files().length)" (click)="send()">
              <mat-icon class="flip-rtl">send</mat-icon> {{ 'help.send' | t }}</button>
          </div>
          @if (d.ticket.status === 'Solved') { <p class="secondary small">{{ 'help.solvedHint' | t }}</p> }
        </mat-card>
      } @else {
        <p class="secondary">{{ 'help.closedHint' | t }}</p>
      }
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: `
    .facts { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; margin-block-end: 16px; }
    .secondary { color: var(--mat-sys-on-surface-variant); }
    .small { font-size: 12px; }
    .banner { padding: 10px 14px; border-radius: 10px; background: #fff4d6; }
    .reply { margin-block-start: 16px; padding: 12px; }
    .full { width: 100%; }
    .actions { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
    .spacer { flex: 1; }
    h1 { overflow-wrap: anywhere; }
  `
})
export class SupportTicketPage implements OnInit {
  readonly id = input.required<string>();
  protected readonly support = inject(SupportService);
  private readonly notify = inject(NotifyService);
  private readonly i18n = inject(I18nService);
  protected readonly t = signal<TicketDetail | null>(null);
  protected readonly files = signal<File[]>([]);
  protected readonly busy = signal(false);
  protected readonly text = new FormControl('', { nonNullable: true });
  protected readonly accept = SUPPORT_FILE_ACCEPT;
  protected readonly chip = STATUS_CHIP;
  protected readonly when = dateTime;

  ngOnInit(): void {
    this.support.ticket(this.id()).subscribe({ next: (t) => this.t.set(t), error: (e) => this.notify.error(e) });
  }

  protected pick(input: HTMLInputElement): void {
    const next = [...this.files(), ...Array.from(input.files ?? [])];
    input.value = '';
    const problem = checkSupportFiles(next);
    if (problem) this.notify.info(this.i18n.t(problem.key, { name: problem.name ?? '' }));
    else this.files.set(next);
  }

  protected send(): void {
    this.busy.set(true);
    this.support.reply(this.id(), this.text.value.trim(), this.files()).subscribe({
      next: (t) => {
        this.t.set(t);
        this.text.setValue('');
        this.files.set([]);
        this.busy.set(false);
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }

  protected solve(): void {
    this.support.solve(this.id()).subscribe({ next: (t) => this.t.set(t), error: (e) => this.notify.error(e) });
  }
}
