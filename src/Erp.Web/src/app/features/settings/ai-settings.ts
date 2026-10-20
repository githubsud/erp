import { Component, inject, OnInit, signal } from '@angular/core';
import { FormBuilder } from '@angular/forms';
import { MatRadioModule } from '@angular/material/radio';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { AiStatus } from '../../core/ai.service';
import { ApiService } from '../../core/api.service';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

interface PlatformProvider {
  code: string;
  name: string;
  isPrivate: boolean;
  isDefault: boolean;
  model: string;
  canReadImages: boolean;
  dailyLimit: number;
}

interface AiSettings {
  mode: 'Platform' | 'Own';
  providerCode: string | null;
  kind: 'OpenAiCompatible' | 'Anthropic' | null;
  baseUrl: string | null;
  model: string | null;
  visionModel: string | null;
  hasApiKey: boolean;
  summaryEnabled: boolean;
  summaryHour: number;
  summaryLanguage: 'ar' | 'en';
  summaryRecipients: string[];
  platformProviders: PlatformProvider[];
  effective: AiStatus | null;
}

interface UsageDay {
  day: string;
  provider: string;
  requests: number;
  inputTokens: number;
  outputTokens: number;
}

interface Preset {
  key: string;
  kind: 'OpenAiCompatible' | 'Anthropic';
  url: string;
  model: string;
  vision: string;
}

/** Example models; the customer types the exact name their subscription offers. */
const PRESETS: Preset[] = [
  { key: 'openai', kind: 'OpenAiCompatible', url: 'https://api.openai.com/v1/', model: 'gpt-4o-mini', vision: 'gpt-4o-mini' },
  { key: 'anthropic', kind: 'Anthropic', url: 'https://api.anthropic.com', model: 'claude-haiku-5-5', vision: 'claude-haiku-5-5' },
  { key: 'gemini', kind: 'OpenAiCompatible', url: 'https://generativelanguage.googleapis.com/v1beta/openai/', model: 'gemini-3.5-flash', vision: 'gemini-3.5-flash' },
  { key: 'groq', kind: 'OpenAiCompatible', url: 'https://api.groq.com/openai/v1/', model: 'llama-3.3-70b-versatile', vision: '' },
  { key: 'openrouter', kind: 'OpenAiCompatible', url: 'https://openrouter.ai/api/v1/', model: 'openrouter/auto', vision: '' },
  { key: 'deepseek', kind: 'OpenAiCompatible', url: 'https://api.deepseek.com/v1/', model: 'deepseek-chat', vision: '' },
  { key: 'mistral', kind: 'OpenAiCompatible', url: 'https://api.mistral.ai/v1/', model: 'mistral-small-latest', vision: '' },
  { key: 'custom', kind: 'OpenAiCompatible', url: '', model: '', vision: '' }
];

/** Which AI the company uses (the platform's free or private model, or its own subscription) and the daily summary. */
@Component({
  selector: 'app-ai-settings',
  imports: [...SHARED, MatRadioModule, MatSlideToggleModule],
  template: `
    <div class="page-header"><h1><mat-icon class="spark">auto_awesome</mat-icon> {{ 'ais.title' | t }}</h1></div>
    @if (settings(); as s) {
      @if (s.effective; as e) {
        <p class="secondary">{{ 'ais.inUse' | t }}: <b>{{ e.name }}</b> · <span class="ltr">{{ e.model }}</span> ·
          {{ (e.isPrivate ? 'ai.private' : 'ai.shared') | t }}
          @if (e.dailyLimit !== null) { · <span class="ltr">{{ e.usedToday }} / {{ e.dailyLimit }}</span> {{ 'ai.today' | t }} }</p>
      }
      <form [formGroup]="form">
        <mat-card appearance="outlined" class="card">
          <h3>{{ 'ais.which' | t }}</h3>
          <mat-radio-group formControlName="mode" class="modes">
            <mat-radio-button value="Platform">{{ 'ais.platform' | t }}</mat-radio-button>
            <mat-radio-button value="Own">{{ 'ais.own' | t }}</mat-radio-button>
          </mat-radio-group>

          @if (form.value.mode === 'Platform') {
            <mat-radio-group formControlName="providerCode" class="providers">
              <mat-radio-button [value]="null">{{ 'ais.recommended' | t }}</mat-radio-button>
              @for (p of s.platformProviders; track p.code) {
                <mat-radio-button [value]="p.code">
                  {{ p.name }} <span class="chip" [class.ok]="p.isPrivate">{{ (p.isPrivate ? 'ai.private' : 'ai.shared') | t }}</span>
                  <span class="secondary ltr">{{ p.dailyLimit }}/{{ 'ai.day' | t }}</span>
                  @if (p.canReadImages) { <span class="chip">{{ 'ais.readsImages' | t }}</span> }
                </mat-radio-button>
              }
            </mat-radio-group>
            <p class="note"><mat-icon>shield</mat-icon> {{ 'ais.privacyNote' | t }}</p>
          } @else {
            <div class="form-grid">
              <mat-form-field><mat-label>{{ 'ais.provider' | t }}</mat-label>
                <mat-select [value]="preset()" (selectionChange)="usePreset($event.value)">
                  @for (p of presets; track p.key) { <mat-option [value]="p.key">{{ 'ais.preset.' + p.key | t }}</mat-option> }
                </mat-select></mat-form-field>
              <mat-form-field class="wide"><mat-label>{{ 'ais.url' | t }}</mat-label><input matInput formControlName="baseUrl" dir="ltr" /></mat-form-field>
              <mat-form-field><mat-label>{{ 'ais.model' | t }}</mat-label><input matInput formControlName="model" dir="ltr" />
                <mat-hint>{{ 'ais.modelHint' | t }}</mat-hint></mat-form-field>
              <mat-form-field><mat-label>{{ 'ais.visionModel' | t }}</mat-label><input matInput formControlName="visionModel" dir="ltr" />
                <mat-hint>{{ 'ais.visionHint' | t }}</mat-hint></mat-form-field>
              <mat-form-field class="wide"><mat-label>{{ 'ais.apiKey' | t }}</mat-label>
                <input matInput type="password" formControlName="apiKey" dir="ltr" autocomplete="new-password"
                  [placeholder]="s.hasApiKey ? ('ais.keySaved' | t) : ''" />
                <mat-hint>{{ 'ais.keyHint' | t }}</mat-hint></mat-form-field>
            </div>
            <p class="note"><mat-icon>lock</mat-icon> {{ 'ais.ownNote' | t }}</p>
          }
          <div class="actions">
            <button mat-stroked-button type="button" [disabled]="testing()" (click)="test()"><mat-icon>network_check</mat-icon> {{ 'ais.test' | t }}</button>
            @if (testing()) { <span class="secondary">{{ 'ai.thinking' | t }}</span> }
            @if (testResult(); as r) {
              <span [class.okText]="r.ok" [class.badText]="!r.ok">{{ r.ok ? ('ais.testOk' | t) : r.error }} <span class="ltr">({{ r.milliseconds }} ms)</span></span>
            }
          </div>
        </mat-card>

        <mat-card appearance="outlined" class="card">
          <h3>{{ 'ais.summary' | t }}</h3>
          <mat-slide-toggle formControlName="summaryEnabled">{{ 'ais.summaryEnabled' | t }}</mat-slide-toggle>
          <div class="form-grid">
            <mat-form-field><mat-label>{{ 'ais.summaryHour' | t }}</mat-label>
              <mat-select formControlName="summaryHour">@for (h of hours; track h) { <mat-option [value]="h"><span class="ltr">{{ h }}:00</span></mat-option> }</mat-select></mat-form-field>
            <mat-form-field><mat-label>{{ 'ais.summaryLanguage' | t }}</mat-label>
              <mat-select formControlName="summaryLanguage"><mat-option value="ar">العربية</mat-option><mat-option value="en">English</mat-option></mat-select></mat-form-field>
            <mat-form-field class="wide"><mat-label>{{ 'ais.recipients' | t }}</mat-label><input matInput formControlName="summaryRecipients" dir="ltr" />
              <mat-hint>{{ 'ais.recipientsHint' | t }}</mat-hint></mat-form-field>
          </div>
        </mat-card>
        <button mat-flat-button type="button" (click)="save()">{{ 'app.save' | t }}</button>
      </form>

      @if (usage().length) {
        <h3>{{ 'ais.usage' | t }}</h3>
        <table class="line-editor">
          <thead><tr><th>{{ 'app.date' | t }}</th><th>{{ 'ais.provider' | t }}</th><th class="num">{{ 'ais.requests' | t }}</th></tr></thead>
          <tbody>@for (u of usage(); track u.day + u.provider) {
            <tr><td class="ltr">{{ u.day | d }}</td><td>{{ u.provider === 'own' ? ('ais.own' | t) : u.provider }}</td><td class="num ltr">{{ u.requests }}</td></tr>
          }</tbody>
        </table>
      }
    }
  `,
  styles: `
    .spark { color: var(--mat-sys-primary); vertical-align: middle; }
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 13px; }
    .card { padding: 16px; margin-bottom: 16px; }
    .card h3 { margin-top: 0; }
    .modes, .providers { display: flex; flex-direction: column; gap: 6px; margin-bottom: 12px; }
    .providers { margin-inline-start: 24px; }
    .note { display: flex; gap: 6px; align-items: flex-start; color: var(--mat-sys-on-surface-variant); font-size: 13px; }
    .note mat-icon { font-size: 18px; width: 18px; height: 18px; flex: none; }
    .wide { grid-column: span 2; min-width: 280px; }
    .actions { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; }
    .okText { color: #1b6e3a; }
    .badText { color: var(--mat-sys-error); }
    .num { text-align: end; }
  `
})
export class AiSettingsPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly presets = PRESETS;
  protected readonly hours = Array.from({ length: 24 }, (_, i) => i);
  protected readonly settings = signal<AiSettings | null>(null);
  protected readonly usage = signal<UsageDay[]>([]);
  protected readonly preset = signal('custom');
  protected readonly testing = signal(false);
  protected readonly testResult = signal<{ ok: boolean; error: string | null; milliseconds: number } | null>(null);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    mode: ['Platform' as 'Platform' | 'Own'],
    providerCode: [null as string | null],
    kind: ['OpenAiCompatible' as 'OpenAiCompatible' | 'Anthropic'],
    baseUrl: [''],
    model: [''],
    visionModel: [''],
    apiKey: [''],
    summaryEnabled: [true],
    summaryHour: [21],
    summaryLanguage: ['ar' as 'ar' | 'en'],
    summaryRecipients: ['']
  });

  ngOnInit(): void {
    this.load();
    this.api.get<UsageDay[]>('ai/usage', { days: 30 }).subscribe((u) => this.usage.set(u));
  }

  private load(): void {
    this.api.get<AiSettings>('ai/settings').subscribe({
      next: (s) => {
        this.settings.set(s);
        this.form.reset({
          mode: s.mode, providerCode: s.providerCode, kind: s.kind ?? 'OpenAiCompatible', baseUrl: s.baseUrl ?? '', model: s.model ?? '',
          visionModel: s.visionModel ?? '', apiKey: '', summaryEnabled: s.summaryEnabled, summaryHour: s.summaryHour, summaryLanguage: s.summaryLanguage,
          summaryRecipients: s.summaryRecipients.join(', ')
        });
        this.preset.set(PRESETS.find((p) => p.url && p.url === s.baseUrl)?.key ?? 'custom');
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected usePreset(key: string): void {
    const p = PRESETS.find((x) => x.key === key)!;
    this.preset.set(key);
    this.form.patchValue({ kind: p.kind, baseUrl: p.url, model: p.model, visionModel: p.vision });
  }

  private body() {
    const v = this.form.getRawValue();
    return {
      mode: v.mode, providerCode: v.providerCode, kind: v.mode === 'Own' ? v.kind : null, baseUrl: v.baseUrl || null, model: v.model || null,
      visionModel: v.visionModel || null, apiKey: v.apiKey ? v.apiKey : null, summaryEnabled: v.summaryEnabled, summaryHour: v.summaryHour,
      summaryLanguage: v.summaryLanguage, summaryRecipients: v.summaryRecipients.split(/[,;\s]+/).filter((x) => x)
    };
  }

  protected test(): void {
    this.testing.set(true);
    this.testResult.set(null);
    this.api.post<{ ok: boolean; error: string | null; milliseconds: number }>('ai/settings/test', this.body()).subscribe({
      next: (r) => {
        this.testResult.set(r);
        this.testing.set(false);
      },
      error: (e) => {
        this.testing.set(false);
        this.notify.error(e);
      }
    });
  }

  protected save(): void {
    this.api.put<AiSettings>('ai/settings', this.body()).subscribe({
      next: () => {
        this.notify.success();
        this.load();
      },
      error: (e) => this.notify.error(e)
    });
  }
}
