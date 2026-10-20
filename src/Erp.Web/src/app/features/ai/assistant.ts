import { Component, ElementRef, inject, OnInit, signal, viewChild } from '@angular/core';
import { FormControl } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { AiService, AiStatus, AiSummary } from '../../core/ai.service';
import { AuthService } from '../../core/auth.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

interface Turn {
  role: 'user' | 'assistant';
  text: string;
  tools?: string[];
}

const SUGGESTIONS = ['today', 'debtors', 'leaks', 'season', 'documents', 'bestsellers', 'reorder', 'profit', 'payroll', 'cash'];

/** "Ask your business": questions in Arabic or English answered from the ERP's reports, and the owner's daily summary. */
@Component({
  selector: 'app-ai-assistant',
  imports: [...SHARED],
  template: `
    <div class="page-header">
      <h1><mat-icon class="spark">auto_awesome</mat-icon> {{ 'ai.title' | t }}</h1>
      @if (status(); as s) {
        <span class="chip" [class.ok]="s.isPrivate" [matTooltip]="s.name + ' — ' + ((s.isPrivate ? 'ai.privateHint' : 'ai.sharedHint') | t)">
          <mat-icon>{{ s.isPrivate ? 'lock' : 'shield' }}</mat-icon> {{ (s.isPrivate ? 'ai.private' : 'ai.shared') | t }}
        </span>
        @if (s.dailyLimit !== null) { <span class="chip ltr">{{ s.usedToday }} / {{ s.dailyLimit }} {{ 'ai.today' | t }}</span> }
      } @else if (statusLoaded()) {
        <span class="chip bad">{{ 'ai.notConfigured' | t }}</span>
      }
    </div>

    @if (canSummary) {
      <mat-card appearance="outlined" class="summary">
        <div class="summary-head">
          <h3><mat-icon>wb_twilight</mat-icon> {{ 'ai.summary.title' | t }}</h3>
          @if (summary(); as s) { <span class="secondary ltr">{{ s.date | d }}</span> }
          <span class="spacer"></span>
          <button mat-stroked-button [disabled]="summaryBusy()" (click)="writeSummary()"><mat-icon>refresh</mat-icon> {{ 'ai.summary.write' | t }}</button>
          @if (summary(); as s) { <a mat-button [href]="s.whatsAppUrl" target="_blank" rel="noopener"><mat-icon>chat</mat-icon> {{ 'ai.share' | t }}</a> }
        </div>
        @if (summaryBusy()) { <mat-progress-bar mode="indeterminate" /> }
        @if (summary(); as s) {
          <div class="summary-text">{{ s.text }}</div>
          @if (s.provider === 'none') { <p class="secondary">{{ 'ai.summary.figuresOnly' | t }}</p> }
        } @else if (!summaryBusy()) { <p class="secondary">{{ 'ai.summary.none' | t }}</p> }
      </mat-card>
    }

    <mat-card appearance="outlined" class="chat">
      <div class="turns" #turnsBox>
        @if (!turns().length) {
          <p class="secondary">{{ 'ai.hint' | t }}</p>
          <div class="suggestions">
            @for (s of suggestions; track s) { <button mat-stroked-button (click)="ask(i18n.t('ai.q.' + s))">{{ 'ai.q.' + s | t }}</button> }
          </div>
        }
        @for (turn of turns(); track $index) {
          <div class="turn" [class.user]="turn.role === 'user'">
            <div class="bubble">{{ turn.text }}</div>
            @if (turn.tools?.length) {
              <div class="sources">{{ 'ai.from' | t }}: @for (tool of turn.tools; track tool; let last = $last) { {{ 'ai.tool.' + tool | t }}@if (!last) {، } }</div>
            }
          </div>
        }
        @if (busy()) {
          <div class="turn"><div class="bubble thinking"><mat-progress-bar mode="indeterminate" /> {{ 'ai.thinking' | t }}</div></div>
        }
      </div>
      <form class="ask" (submit)="$event.preventDefault(); ask(question.value)">
        <mat-form-field class="grow" subscriptSizing="dynamic">
          <input matInput [formControl]="question" [placeholder]="'ai.placeholder' | t" maxlength="1000" autocomplete="off" />
        </mat-form-field>
        <button mat-flat-button type="submit" [disabled]="busy() || !question.value.trim()"><mat-icon class="flip-rtl">send</mat-icon> {{ 'ai.ask' | t }}</button>
        @if (turns().length) { <button mat-button type="button" (click)="turns.set([])">{{ 'ai.newChat' | t }}</button> }
      </form>
    </mat-card>
  `,
  styles: `
    .spark { color: var(--mat-sys-primary); vertical-align: middle; }
    .page-header { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
    .chip mat-icon { font-size: 16px; width: 16px; height: 16px; vertical-align: text-bottom; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 13px; }
    .summary { padding: 12px 16px; margin-bottom: 16px; }
    .summary-head { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
    .summary-head h3 { margin: 0; display: flex; align-items: center; gap: 6px; }
    .summary-text { white-space: pre-line; line-height: 1.8; margin-top: 8px; }
    .spacer { flex: 1; }
    .chat { padding: 12px 16px; }
    .turns { min-height: 240px; max-height: 60vh; overflow-y: auto; display: flex; flex-direction: column; gap: 10px; padding-block: 8px; }
    .suggestions { display: flex; flex-wrap: wrap; gap: 8px; }
    .turn { display: flex; flex-direction: column; align-items: flex-start; }
    .turn.user { align-items: flex-end; }
    .bubble { max-width: min(720px, 92%); padding: 10px 14px; border-radius: 16px; white-space: pre-line; line-height: 1.7;
      background: var(--mat-sys-surface-container-high); }
    .turn.user .bubble { background: var(--mat-sys-primary-container); color: var(--mat-sys-on-primary-container); }
    .thinking { min-width: 220px; }
    .sources { font-size: 12px; color: var(--mat-sys-on-surface-variant); margin-top: 4px; }
    .ask { display: flex; gap: 8px; align-items: center; margin-top: 8px; flex-wrap: wrap; }
    .grow { flex: 1; min-width: 220px; }
  `
})
export class AssistantPage implements OnInit {
  private readonly ai = inject(AiService);
  private readonly notify = inject(NotifyService);
  protected readonly i18n = inject(I18nService);
  private readonly route = inject(ActivatedRoute);
  protected readonly canSummary = inject(AuthService).can('ai.summary:view');
  protected readonly suggestions = SUGGESTIONS;
  protected readonly status = signal<AiStatus | null>(null);
  protected readonly statusLoaded = signal(false);
  protected readonly summary = signal<AiSummary | null>(null);
  protected readonly summaryBusy = signal(false);
  protected readonly turns = signal<Turn[]>([]);
  protected readonly busy = signal(false);
  protected readonly question = new FormControl('', { nonNullable: true });
  private readonly turnsBox = viewChild<ElementRef<HTMLElement>>('turnsBox');

  ngOnInit(): void {
    this.ai.status().subscribe({
      next: (s) => {
        this.status.set(s);
        this.statusLoaded.set(true);
      },
      error: () => this.statusLoaded.set(true)
    });
    if (this.canSummary) this.ai.latestSummary(this.i18n.lang()).subscribe((s) => this.summary.set(s));
    // Other screens send a question ("explain these leaks"): ask it straight away.
    const q = this.route.snapshot.queryParamMap.get('q');
    if (q) this.ask(q);
  }

  protected ask(text: string): void {
    const question = text.trim();
    if (!question || this.busy()) return;
    const history = this.turns().map((t) => ({ role: t.role, text: t.text }));
    this.turns.update((t) => [...t, { role: 'user', text: question }]);
    this.question.setValue('');
    this.busy.set(true);
    this.scroll();
    this.ai.ask(question, history).subscribe({
      next: (a) => {
        this.turns.update((t) => [...t, { role: 'assistant', text: a.answer || this.i18n.t('ai.noAnswer'), tools: a.toolsUsed }]);
        this.busy.set(false);
        this.refreshStatus();
        this.scroll();
      },
      error: (e) => {
        this.busy.set(false);
        this.turns.update((t) => t.slice(0, -1));
        this.question.setValue(question);
        this.notify.error(e);
      }
    });
  }

  protected writeSummary(): void {
    this.summaryBusy.set(true);
    this.ai.writeSummary(this.i18n.lang()).subscribe({
      next: (s) => {
        this.summary.set(s);
        this.summaryBusy.set(false);
        this.refreshStatus();
      },
      error: (e) => {
        this.summaryBusy.set(false);
        this.notify.error(e);
      }
    });
  }

  private refreshStatus(): void {
    this.ai.status().subscribe((s) => this.status.set(s));
  }

  private scroll(): void {
    setTimeout(() => {
      const box = this.turnsBox()?.nativeElement;
      if (box) box.scrollTop = box.scrollHeight;
    });
  }
}
