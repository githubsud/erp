import { Component, OnInit, effect, inject, input, signal, untracked } from '@angular/core';
import { FormBuilder, FormControl, Validators } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { BackOfficeService } from '../../core/backoffice.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { NotifyService } from '../../core/notify.service';
import {
  SUPPORT_FILE_ACCEPT, TICKET_PRIORITIES, TICKET_STATUSES, TICKET_TYPES, TYPE_ICON, TicketDetail, TicketFile, TicketRow, TicketStatus,
  checkSupportFiles, openBlob
} from '../../core/support.service';
import { SHARED } from '../../shared/shared';
import { TicketThread, dateTime } from '../../shared/ticket-thread';
import { STATUS_CHIP } from '../support/support';

interface Counts {
  open: number;
  new: number;
  overdue: number;
  unread: number;
}

interface Suggestion {
  id: string;
  ticketNo: string;
  subject: string;
  type: string;
  status: TicketStatus;
  shops: number;
  requests: number;
  firstAskedAt: string;
  lastAskedAt: string;
}

interface SavedReply {
  id: string;
  title: string;
  bodyAr: string;
  bodyEn: string;
}

const FILTERS = ['open', 'unread', 'overdue', 'New', 'WaitingCustomer', 'Solved', 'Closed', 'all'] as const;

/** Every customer's requests: inbox with filters, the suggestions most asked for, and the saved replies. */
@Component({
  selector: 'app-bo-support',
  imports: [...SHARED],
  template: `
    <div class="page-header"><h1>{{ 'help.nav' | t }}</h1></div>
    @if (counts(); as c) {
      <div class="card-grid">
        <mat-card appearance="outlined" class="stat" (click)="setFilter('open')"><div class="label">{{ 'help.f.open' | t }}</div><div class="value">{{ c.open }}</div></mat-card>
        <mat-card appearance="outlined" class="stat" (click)="setFilter('unread')"><div class="label">{{ 'help.f.unread' | t }}</div><div class="value">{{ c.unread }}</div></mat-card>
        <mat-card appearance="outlined" class="stat" [class.warn]="c.overdue > 0" (click)="setFilter('overdue')"><div class="label">{{ 'help.f.overdue' | t }}</div>
          <div class="value">{{ c.overdue }}</div><div class="hint">{{ 'help.overdueHint' | t }}</div></mat-card>
      </div>
    }
    <mat-tab-group mat-stretch-tabs="false" (selectedIndexChange)="tab.set($event)">
      <mat-tab [label]="'help.tab.requests' | t">
        <div class="filters">
          <mat-chip-listbox [value]="filter()" (change)="setFilter($event.value)">
            @for (f of filters; track f) { <mat-chip-option [value]="f">{{ 'help.f.' + f | t }}</mat-chip-option> }
          </mat-chip-listbox>
          <mat-form-field class="narrow"><mat-label>{{ 'help.typeLabel' | t }}</mat-label>
            <mat-select [formControl]="type" (selectionChange)="load()"><mat-option value="">{{ 'help.anyType' | t }}</mat-option>
              @for (ty of types; track ty) { <mat-option [value]="ty">{{ 'help.type.' + ty | t }}</mat-option> }</mat-select></mat-form-field>
          <mat-form-field class="narrow"><mat-label>{{ 'app.search' | t }}</mat-label><input matInput [formControl]="search" (keyup.enter)="load()" /></mat-form-field>
        </div>
        <div class="list">
          @for (r of rows(); track r.id) {
            <a class="row" [routerLink]="['/backoffice/support', r.id]" [class.unread]="r.unread">
              <mat-icon class="type">{{ icon[r.type] }}</mat-icon>
              <div class="main">
                <div class="subject" dir="auto">{{ r.subject }}</div>
                <div class="secondary small"><span class="ltr">{{ r.ticketNo }}</span> · <b class="ltr">{{ r.customerCode }}</b> {{ r.customerName }} · {{ r.requesterName }}
                  · <span class="ltr">{{ when(r.lastActivityAt) }}</span>
                  @if (r.duplicateOfNo) { · ↪ <span class="ltr">{{ r.duplicateOfNo }}</span> }</div>
              </div>
              @if (r.overdue) { <span class="chip bad">{{ 'help.f.overdue' | t }}</span> }
              @if (r.priority === 'High' || r.priority === 'Urgent') { <span class="chip warn">{{ 'help.prio.' + r.priority | t }}</span> }
              <span [class]="'chip ' + chip[r.status]">{{ 'help.status.' + r.status | t }}</span>
            </a>
          }
        </div>
        @if (!rows().length) { <p class="secondary">{{ 'app.none' | t }}</p> }
      </mat-tab>

      <mat-tab [label]="'help.tab.suggestions' | t">
        <p class="secondary">{{ 'help.suggestionsHint' | t }}</p>
        <table class="line-editor">
          <thead><tr><th>{{ 'help.subject' | t }}</th><th class="num">{{ 'help.shops' | t }}</th><th class="num">{{ 'help.requests' | t }}</th>
            <th>{{ 'help.statusLabel' | t }}</th><th>{{ 'help.lastAsked' | t }}</th></tr></thead>
          <tbody>
            @for (s of suggestions(); track s.id) {
              <tr class="clickable" [routerLink]="['/backoffice/support', s.id]">
                <td><span class="ltr secondary">{{ s.ticketNo }}</span> <span dir="auto">{{ s.subject }}</span></td>
                <td class="num"><b>{{ s.shops }}</b></td><td class="num">{{ s.requests }}</td>
                <td><span [class]="'chip ' + chip[s.status]">{{ 'help.status.' + s.status | t }}</span></td>
                <td class="ltr">{{ when(s.lastAskedAt) }}</td>
              </tr>
            }
          </tbody>
        </table>
        @if (!suggestions().length) { <p class="secondary">{{ 'app.none' | t }}</p> }
      </mat-tab>

      <mat-tab [label]="'help.tab.replies' | t">
        <p class="secondary">{{ 'help.repliesHint' | t }}</p>
        @for (r of replies(); track r.id) {
          <mat-card appearance="outlined" class="saved">
            <div class="head"><b>{{ r.title }}</b><span class="spacer"></span>
              <button mat-icon-button (click)="editReply(r)" [attr.aria-label]="'app.edit' | t"><mat-icon>edit</mat-icon></button>
              <button mat-icon-button (click)="deleteReply(r)" [attr.aria-label]="'app.delete' | t"><mat-icon>delete</mat-icon></button></div>
            <p dir="rtl" class="body">{{ r.bodyAr }}</p><p dir="ltr" class="body">{{ r.bodyEn }}</p>
          </mat-card>
        }
        <mat-card appearance="outlined" class="saved">
          <h3>{{ (editingReply() ? 'help.editReply' : 'help.newReply') | t }}</h3>
          <form [formGroup]="replyForm">
            <mat-form-field class="full"><mat-label>{{ 'help.replyTitle' | t }}</mat-label><input matInput formControlName="title" /></mat-form-field>
            <mat-form-field class="full"><mat-label>{{ 'help.replyAr' | t }}</mat-label><textarea matInput formControlName="bodyAr" rows="3" dir="rtl"></textarea></mat-form-field>
            <mat-form-field class="full"><mat-label>{{ 'help.replyEn' | t }}</mat-label><textarea matInput formControlName="bodyEn" rows="3" dir="ltr"></textarea></mat-form-field>
          </form>
          <div class="actions"><button mat-flat-button [disabled]="replyForm.invalid" (click)="saveReply()">{{ 'app.save' | t }}</button>
            @if (editingReply()) { <button mat-button (click)="resetReply()">{{ 'app.cancel' | t }}</button> }</div>
        </mat-card>
      </mat-tab>
    </mat-tab-group>
  `,
  styles: `
    .stat { cursor: pointer; } .stat.warn .value { color: #8c1d18; }
    .filters { display: flex; flex-wrap: wrap; gap: 12px; align-items: center; margin-block: 12px; }
    .narrow { width: 200px; }
    .secondary { color: var(--mat-sys-on-surface-variant); }
    .small { font-size: 12px; }
    .list { display: flex; flex-direction: column; gap: 8px; }
    .row { display: flex; gap: 12px; align-items: center; padding: 10px 14px; border: 1px solid var(--mat-sys-outline-variant); border-radius: 12px;
      color: inherit; text-decoration: none; background: var(--mat-sys-surface); }
    .row.unread { border-color: var(--mat-sys-primary); background: var(--mat-sys-surface-container-low); }
    .row.unread .subject { font-weight: 600; }
    .type { color: var(--mat-sys-primary); flex: none; }
    .main { flex: 1; min-width: 0; }
    .subject { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .chip.bad { background: #fde7e6; color: #8c1d18; }
    .num { text-align: end; }
    .clickable { cursor: pointer; }
    .saved { padding: 12px 16px; margin-block: 12px; }
    .head { display: flex; align-items: center; }
    .spacer { flex: 1; }
    .body { white-space: pre-wrap; margin: 4px 0; }
    .full { width: 100%; }
    .actions { display: flex; gap: 8px; }
  `
})
export class BackOfficeSupportPage implements OnInit {
  private readonly bo = inject(BackOfficeService);
  private readonly notify = inject(NotifyService);
  private readonly route = inject(ActivatedRoute);
  protected readonly filters = FILTERS;
  protected readonly types = TICKET_TYPES;
  protected readonly icon = TYPE_ICON;
  protected readonly chip = STATUS_CHIP;
  protected readonly when = dateTime;
  protected readonly tab = signal(0);
  protected readonly filter = signal<string>(this.route.snapshot.queryParamMap.get('status') ?? 'open');
  protected readonly type = new FormControl('', { nonNullable: true });
  protected readonly search = new FormControl('', { nonNullable: true });
  protected readonly rows = signal<TicketRow[]>([]);
  protected readonly counts = signal<Counts | null>(null);
  protected readonly suggestions = signal<Suggestion[]>([]);
  protected readonly replies = signal<SavedReply[]>([]);
  protected readonly editingReply = signal<SavedReply | null>(null);
  protected readonly replyForm = inject(FormBuilder).nonNullable.group({
    title: ['', Validators.required],
    bodyAr: ['', Validators.required],
    bodyEn: ['', Validators.required]
  });

  ngOnInit(): void {
    this.load();
    this.bo.get<Suggestion[]>('support/suggestions').subscribe((s) => this.suggestions.set(s));
    this.bo.get<SavedReply[]>('support/saved-replies').subscribe((r) => this.replies.set(r));
  }

  protected setFilter(f: string | null): void {
    this.filter.set(f ?? 'all');
    this.load();
  }

  protected load(): void {
    const status = this.filter() === 'all' ? null : this.filter();
    this.bo.get<TicketRow[]>('support/tickets', { status, type: this.type.value, search: this.search.value })
      .subscribe({ next: (r) => this.rows.set(r), error: (e) => this.notify.error(e) });
    this.bo.get<Counts>('support/counts').subscribe((c) => this.counts.set(c));
  }

  protected editReply(r: SavedReply): void {
    this.editingReply.set(r);
    this.replyForm.reset({ title: r.title, bodyAr: r.bodyAr, bodyEn: r.bodyEn });
  }

  protected resetReply(): void {
    this.editingReply.set(null);
    this.replyForm.reset();
  }

  protected saveReply(): void {
    const editing = this.editingReply();
    const call = editing ? this.bo.put<SavedReply[]>(`support/saved-replies/${editing.id}`, this.replyForm.getRawValue())
      : this.bo.post<SavedReply[]>('support/saved-replies', this.replyForm.getRawValue());
    call.subscribe({
      next: (r) => {
        this.replies.set(r);
        this.resetReply();
        this.notify.success();
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected deleteReply(r: SavedReply): void {
    this.bo.delete<SavedReply[]>(`support/saved-replies/${r.id}`).subscribe({ next: (list) => this.replies.set(list), error: (e) => this.notify.error(e) });
  }
}

/** One request: the conversation with internal notes, status/priority/type, duplicates, and the answer box with saved replies. */
@Component({
  selector: 'app-bo-ticket',
  imports: [...SHARED, TicketThread],
  template: `
    @if (t(); as d) {
      <div class="page-header">
        <a mat-icon-button routerLink="/backoffice/support" [attr.aria-label]="'app.back' | t"><mat-icon class="flip-rtl">arrow_back</mat-icon></a>
        <h1 dir="auto">{{ d.ticket.subject }}</h1>
      </div>
      <div class="layout">
        <div class="conversation">
          <app-ticket-thread [messages]="d.messages" mine="Admin" (openFile)="open($event)" />
          <mat-card appearance="outlined" class="reply" [class.note]="internal.value">
            <div class="reply-head">
              <mat-slide-toggle [formControl]="internal">{{ 'help.internalNote' | t }}</mat-slide-toggle>
              <span class="spacer"></span>
              @if (replies().length && !internal.value) {
                <button mat-button [matMenuTriggerFor]="savedMenu"><mat-icon>quickreply</mat-icon> {{ 'help.savedReplies' | t }}</button>
                <mat-menu #savedMenu="matMenu">
                  @for (r of replies(); track r.id) { <button mat-menu-item (click)="useReply(r)">{{ r.title }}</button> }
                </mat-menu>
              }
            </div>
            <mat-form-field class="full"><mat-label>{{ (internal.value ? 'help.notePlaceholder' : 'help.answerPlaceholder') | t }}</mat-label>
              <textarea matInput [formControl]="text" rows="5" [dir]="d.language === 'ar' ? 'rtl' : 'ltr'"></textarea>
              <mat-hint>{{ 'help.answerLanguage' | t }}: {{ (d.language === 'ar' ? 'help.arabic' : 'help.english') | t }}</mat-hint></mat-form-field>
            <div class="actions">
              <button mat-stroked-button (click)="picker.click()"><mat-icon>attach_file</mat-icon> {{ 'help.attach' | t }}</button>
              <input #picker type="file" multiple hidden [accept]="accept" (change)="pick(picker)" />
              @for (f of files(); track f.name) { <span class="chip">{{ f.name }}</span> }
              <span class="spacer"></span>
              @if (!internal.value) {
                <mat-form-field class="after"><mat-label>{{ 'help.afterSending' | t }}</mat-label>
                  <mat-select [formControl]="after">@for (s of statuses; track s) { <mat-option [value]="s">{{ 'help.status.' + s | t }}</mat-option> }</mat-select></mat-form-field>
              }
              <button mat-flat-button [disabled]="busy() || (!text.value.trim() && !files().length)" (click)="send()">
                <mat-icon class="flip-rtl">send</mat-icon> {{ (internal.value ? 'help.addNote' : 'help.sendAnswer') | t }}</button>
            </div>
          </mat-card>
        </div>

        <mat-card appearance="outlined" class="side">
          <div class="fact"><span class="label">{{ 'help.number' | t }}</span><b class="ltr">{{ d.ticket.ticketNo }}</b></div>
          <div class="fact"><span class="label">{{ 'bo.customer' | t }}</span>
            <a [routerLink]="['/backoffice/customers', d.ticket.tenantId]"><b class="ltr">{{ d.ticket.customerCode }}</b> {{ d.ticket.customerName }}</a></div>
          <div class="fact"><span class="label">{{ 'help.requester' | t }}</span><span>{{ d.ticket.requesterName }}</span>
            @if (d.requesterEmail) { <span class="ltr small">{{ d.requesterEmail }}</span> }</div>
          <div class="fact"><span class="label">{{ 'help.opened' | t }}</span><span class="ltr">{{ when(d.ticket.createdAt) }}</span></div>
          <div class="fact" [class.bad]="d.ticket.overdue"><span class="label">{{ 'help.answerBy' | t }}</span>
            <span class="ltr">{{ d.ticket.firstResponseAt ? ('✓ ' + when(d.ticket.firstResponseAt)) : when(d.ticket.responseDueAt) }}</span></div>
          @if (d.page) { <div class="fact"><span class="label">{{ 'help.page' | t }}</span><span class="ltr small">{{ d.page }}</span></div> }
          @if (d.device) { <div class="fact"><span class="label">{{ 'help.device' | t }}</span><span class="ltr small device">{{ d.device }}</span></div> }

          <mat-form-field class="full"><mat-label>{{ 'help.statusLabel' | t }}</mat-label>
            <mat-select [value]="d.ticket.status" (selectionChange)="update({ status: $event.value })">
              @for (s of statuses; track s) { <mat-option [value]="s">{{ 'help.status.' + s | t }}</mat-option> }</mat-select></mat-form-field>
          <mat-form-field class="full"><mat-label>{{ 'help.priority' | t }}</mat-label>
            <mat-select [value]="d.ticket.priority" (selectionChange)="update({ priority: $event.value })">
              @for (p of priorities; track p) { <mat-option [value]="p">{{ 'help.prio.' + p | t }}</mat-option> }</mat-select></mat-form-field>
          <mat-form-field class="full"><mat-label>{{ 'help.typeLabel' | t }}</mat-label>
            <mat-select [value]="d.ticket.type" (selectionChange)="update({ type: $event.value })">
              @for (ty of types; track ty) { <mat-option [value]="ty">{{ 'help.type.' + ty | t }}</mat-option> }</mat-select></mat-form-field>

          <div class="dup">
            @if (d.ticket.duplicateOfNo) {
              <p>{{ 'help.sameAs' | t }} <b class="ltr">{{ d.ticket.duplicateOfNo }}</b>
                <button mat-button (click)="update({ clearDuplicate: true })">{{ 'help.notSame' | t }}</button></p>
            } @else {
              <mat-form-field class="full"><mat-label>{{ 'help.markSame' | t }}</mat-label>
                <input matInput [formControl]="duplicateOf" placeholder="SUP-1001" dir="ltr" (keyup.enter)="markDuplicate()" />
                <mat-hint>{{ 'help.markSameHint' | t }}</mat-hint></mat-form-field>
              <button mat-stroked-button [disabled]="!duplicateOf.value.trim()" (click)="markDuplicate()">{{ 'app.save' | t }}</button>
            }
          </div>
          @if (d.duplicates.length) {
            <div class="label">{{ 'help.alsoAsked' | t: { n: d.duplicates.length } }}</div>
            @for (x of d.duplicates; track x.id) {
              <a class="dup-row" [routerLink]="['/backoffice/support', x.id]"><span class="ltr">{{ x.ticketNo }}</span> · {{ x.customerCode }}</a>
            }
          }
        </mat-card>
      </div>
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
  `,
  styles: `
    .layout { display: grid; grid-template-columns: minmax(0, 1fr) 300px; gap: 16px; align-items: start; }
    @media (max-width: 900px) { .layout { grid-template-columns: 1fr; } }
    .reply { margin-block-start: 16px; padding: 12px; }
    .reply.note { background: #fff4d6; }
    .reply-head { display: flex; align-items: center; }
    .full { width: 100%; }
    .actions { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
    .after { width: 200px; }
    .spacer { flex: 1; }
    .side { padding: 16px; }
    .fact { display: flex; flex-direction: column; margin-block-end: 10px; }
    .fact.bad { color: #8c1d18; }
    .label { font-size: 12px; color: var(--mat-sys-on-surface-variant); }
    .small { font-size: 12px; }
    .device { overflow-wrap: anywhere; }
    .dup { margin-block: 8px; }
    .dup-row { display: block; font-size: 13px; padding: 2px 0; }
    h1 { overflow-wrap: anywhere; }
  `
})
export class BackOfficeTicketPage {
  readonly id = input.required<string>();
  private readonly bo = inject(BackOfficeService);
  private readonly notify = inject(NotifyService);
  private readonly i18n = inject(I18nService);
  protected readonly t = signal<TicketDetail | null>(null);
  protected readonly replies = signal<SavedReply[]>([]);
  protected readonly files = signal<File[]>([]);
  protected readonly busy = signal(false);
  protected readonly statuses = TICKET_STATUSES;
  protected readonly priorities = TICKET_PRIORITIES;
  protected readonly types = TICKET_TYPES;
  protected readonly accept = SUPPORT_FILE_ACCEPT;
  protected readonly when = dateTime;
  protected readonly text = new FormControl('', { nonNullable: true });
  protected readonly internal = new FormControl(false, { nonNullable: true });
  protected readonly after = new FormControl<TicketStatus>('WaitingCustomer', { nonNullable: true });
  protected readonly duplicateOf = new FormControl('', { nonNullable: true });

  constructor() {
    // Follows links between duplicates, which reuse this page.
    effect(() => {
      const id = this.id();
      untracked(() => this.load(id));
    });
    this.bo.get<SavedReply[]>('support/saved-replies').subscribe((r) => this.replies.set(r));
  }

  private load(id: string): void {
    this.t.set(null);
    this.bo.get<TicketDetail>(`support/tickets/${id}`).subscribe({ next: (t) => this.t.set(t), error: (e) => this.notify.error(e) });
  }

  protected useReply(r: SavedReply): void {
    const body = this.t()?.language === 'en' ? r.bodyEn : r.bodyAr;
    this.text.setValue(this.text.value.trim() ? `${this.text.value.trim()}\n\n${body}` : body);
  }

  protected pick(input: HTMLInputElement): void {
    const next = [...this.files(), ...Array.from(input.files ?? [])];
    input.value = '';
    const problem = checkSupportFiles(next);
    if (problem) this.notify.info(this.i18n.t(problem.key, { name: problem.name ?? '' }));
    else this.files.set(next);
  }

  protected send(): void {
    const form = new FormData();
    form.append('body', this.text.value.trim());
    if (this.internal.value) form.append('isInternal', 'true');
    else form.append('status', this.after.value);
    for (const f of this.files()) form.append('files', f, f.name);
    this.busy.set(true);
    this.bo.post<TicketDetail>(`support/tickets/${this.id()}/messages`, form).subscribe({
      next: (t) => {
        this.t.set(t);
        this.text.setValue('');
        this.files.set([]);
        this.internal.setValue(false);
        this.busy.set(false);
      },
      error: (e) => {
        this.busy.set(false);
        this.notify.error(e);
      }
    });
  }

  protected update(change: Record<string, unknown>): void {
    this.bo.put<TicketDetail>(`support/tickets/${this.id()}`, change).subscribe({
      next: (t) => {
        this.t.set(t);
        this.duplicateOf.setValue('');
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected markDuplicate(): void {
    if (this.duplicateOf.value.trim()) this.update({ duplicateOfNo: this.duplicateOf.value.trim() });
  }

  protected open(file: TicketFile): void {
    this.bo.blob(`support/attachments/${file.id}`).subscribe({ next: (b) => openBlob(b, file), error: (e) => this.notify.error(e) });
  }
}
