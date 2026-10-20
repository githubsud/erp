import { DecimalPipe } from '@angular/common';
import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { BackOfficeService, CustomerRow } from '../../core/backoffice.service';
import { NotifyService } from '../../core/notify.service';
import { SHARED } from '../../shared/shared';

interface Provider {
  code: string;
  name: string;
  kind: 'OpenAiCompatible' | 'Anthropic';
  baseUrl: string;
  model: string;
  visionModel: string | null;
  hasApiKey: boolean;
  isPrivate: boolean;
  isDefault: boolean;
  isEnabled: boolean;
  dailyLimit: number;
}

interface Usage {
  tenantId: string;
  provider: string;
  requests: number;
  inputTokens: number;
  outputTokens: number;
  requestsToday: number;
}

/** The platform's AI: which providers customers can use (the free Gemini tier, our own Ollama server…), the default, and usage per customer. */
@Component({
  selector: 'app-backoffice-ai',
  imports: [...SHARED, MatSlideToggleModule, DecimalPipe],
  template: `
    <div class="page-header"><h1>{{ 'bo.nav.ai' | t }}</h1></div>
    <p class="secondary">{{ 'boai.hint' | t }}</p>
    <div class="providers">
      @for (p of providers(); track p.code) {
        <mat-card appearance="outlined" class="provider" [class.off]="!p.isEnabled">
          <div class="head">
            <b>{{ p.name }}</b>
            @if (p.isDefault) { <span class="chip ok">{{ 'boai.default' | t }}</span> }
            <span class="chip" [class.ok]="p.isPrivate">{{ (p.isPrivate ? 'ai.private' : 'ai.shared') | t }}</span>
            @if (!p.isEnabled) { <span class="chip">{{ 'boai.off' | t }}</span> }
          </div>
          <div class="secondary ltr">{{ p.baseUrl }} · {{ p.model }} @if (p.visionModel) { · 👁 {{ p.visionModel }} }</div>
          <div class="secondary">{{ 'boai.limit' | t }}: <span class="ltr">{{ p.dailyLimit }}</span> · {{ 'ais.apiKey' | t }}: {{ (p.hasApiKey ? 'boai.keySet' : 'boai.noKey') | t }}</div>
          <div class="actions">
            <button mat-button (click)="edit(p)"><mat-icon>edit</mat-icon> {{ 'app.edit' | t }}</button>
            <button mat-button [disabled]="testing() === p.code" (click)="test(p)"><mat-icon>network_check</mat-icon> {{ 'ais.test' | t }}</button>
            @if (results()[p.code]; as r) { <span [class.okText]="r.ok" [class.badText]="!r.ok">{{ r.ok ? ('ais.testOk' | t) : r.error }} <span class="ltr">({{ r.milliseconds }} ms)</span></span> }
          </div>
        </mat-card>
      }
    </div>

    <mat-card appearance="outlined" class="form-card">
      <h3>{{ (editing() ? 'boai.editProvider' : 'boai.newProvider') | t }}</h3>
      <form [formGroup]="form" class="form-grid">
        <mat-form-field><mat-label>{{ 'boai.code' | t }}</mat-label><input matInput formControlName="code" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'bo.nameEn' | t }}</mat-label><input matInput formControlName="name" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'boai.kind' | t }}</mat-label>
          <mat-select formControlName="kind"><mat-option value="OpenAiCompatible">OpenAI-compatible (Gemini, Groq, Ollama…)</mat-option><mat-option value="Anthropic">Anthropic (Claude)</mat-option></mat-select></mat-form-field>
        <mat-form-field class="wide"><mat-label>{{ 'ais.url' | t }}</mat-label><input matInput formControlName="baseUrl" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'ais.model' | t }}</mat-label><input matInput formControlName="model" dir="ltr" /></mat-form-field>
        <mat-form-field><mat-label>{{ 'ais.visionModel' | t }}</mat-label><input matInput formControlName="visionModel" dir="ltr" /></mat-form-field>
        <mat-form-field class="wide"><mat-label>{{ 'ais.apiKey' | t }}</mat-label>
          <input matInput type="password" formControlName="apiKey" dir="ltr" autocomplete="new-password" [placeholder]="editing()?.hasApiKey ? ('ais.keySaved' | t) : ''" />
          <mat-hint>{{ 'boai.keyHint' | t }}</mat-hint></mat-form-field>
        <mat-form-field><mat-label>{{ 'boai.limit' | t }}</mat-label><input matInput type="number" min="0" formControlName="dailyLimit" dir="ltr" /></mat-form-field>
        <div class="toggles">
          <mat-slide-toggle formControlName="isEnabled">{{ 'boai.enabled' | t }}</mat-slide-toggle>
          <mat-slide-toggle formControlName="isDefault">{{ 'boai.default' | t }}</mat-slide-toggle>
          <mat-slide-toggle formControlName="isPrivate">{{ 'boai.isPrivate' | t }}</mat-slide-toggle>
        </div>
      </form>
      <p class="secondary">{{ 'boai.privateHint' | t }}</p>
      <div class="actions">
        <button mat-flat-button [disabled]="form.invalid" (click)="save()">{{ 'app.save' | t }}</button>
        @if (editing()) { <button mat-button (click)="reset()">{{ 'app.cancel' | t }}</button> }
      </div>
    </mat-card>

    <h3>{{ 'boai.usage' | t }}</h3>
    <table class="line-editor">
      <thead><tr><th>{{ 'bo.customer' | t }}</th><th>{{ 'ais.provider' | t }}</th><th class="num">{{ 'boai.today' | t }}</th><th class="num">{{ 'boai.month' | t }}</th>
        <th class="num">{{ 'boai.tokens' | t }}</th></tr></thead>
      <tbody>
        @for (u of usageRows(); track u.tenantId + u.provider) {
          <tr><td>{{ u.customer }}</td><td>{{ u.provider === 'own' ? ('ais.own' | t) : u.provider }}</td><td class="num ltr">{{ u.requestsToday }}</td>
            <td class="num ltr">{{ u.requests }}</td><td class="num ltr">{{ (u.inputTokens + u.outputTokens) | number }}</td></tr>
        }
      </tbody>
    </table>
    @if (!usageRows().length) { <p class="secondary">{{ 'app.none' | t }}</p> }
  `,
  styles: `
    .secondary { color: var(--mat-sys-on-surface-variant); font-size: 13px; }
    .providers { display: grid; grid-template-columns: repeat(auto-fill, minmax(320px, 1fr)); gap: 12px; margin-bottom: 16px; }
    .provider { padding: 12px 16px; }
    .provider.off { opacity: 0.6; }
    .head { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin-bottom: 4px; }
    .actions { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin-top: 8px; }
    .form-card { padding: 16px; margin-bottom: 16px; }
    .form-card h3 { margin-top: 0; }
    .wide { grid-column: span 2; min-width: 280px; }
    .toggles { display: flex; flex-direction: column; gap: 8px; }
    .okText { color: #1b6e3a; }
    .badText { color: var(--mat-sys-error); }
    .num { text-align: end; }
  `
})
export class BackOfficeAiPage implements OnInit {
  private readonly bo = inject(BackOfficeService);
  private readonly notify = inject(NotifyService);
  protected readonly providers = signal<Provider[]>([]);
  protected readonly editing = signal<Provider | null>(null);
  protected readonly testing = signal<string | null>(null);
  protected readonly results = signal<Record<string, { ok: boolean; error: string | null; milliseconds: number }>>({});
  private readonly usage = signal<Usage[]>([]);
  private readonly customers = signal<CustomerRow[]>([]);
  protected readonly usageRows = computed(() => {
    const names = new Map(this.customers().map((c) => [c.id, `${c.code} · ${c.nameEn}`]));
    return this.usage().map((u) => ({ ...u, customer: names.get(u.tenantId) ?? u.tenantId }));
  });
  protected readonly form = inject(FormBuilder).nonNullable.group({
    code: ['', Validators.required],
    name: ['', Validators.required],
    kind: ['OpenAiCompatible' as Provider['kind']],
    baseUrl: ['', Validators.required],
    model: ['', Validators.required],
    visionModel: [''],
    apiKey: [''],
    dailyLimit: [50],
    isEnabled: [true],
    isDefault: [false],
    isPrivate: [false]
  });

  ngOnInit(): void {
    this.load();
    this.bo.get<Usage[]>('ai/usage', { days: 30 }).subscribe((u) => this.usage.set(u));
    this.bo.get<CustomerRow[]>('customers').subscribe((c) => this.customers.set(c));
  }

  private load(): void {
    this.bo.get<Provider[]>('ai/providers').subscribe({ next: (p) => this.providers.set(p), error: (e) => this.notify.error(e) });
  }

  protected edit(p: Provider): void {
    this.editing.set(p);
    this.form.reset({ ...p, visionModel: p.visionModel ?? '', apiKey: '' });
    this.form.controls.code.disable();
  }

  protected reset(): void {
    this.editing.set(null);
    this.form.reset();
    this.form.controls.code.enable();
  }

  protected save(): void {
    const v = this.form.getRawValue();
    const body = { ...v, visionModel: v.visionModel || null, apiKey: v.apiKey ? v.apiKey : null, dailyLimit: Number(v.dailyLimit) };
    const editing = this.editing();
    const call = editing ? this.bo.put<Provider[]>(`ai/providers/${editing.code}`, body) : this.bo.post<Provider[]>('ai/providers', body);
    call.subscribe({
      next: (p) => {
        this.providers.set(p);
        this.notify.success();
        this.reset();
      },
      error: (e) => this.notify.error(e)
    });
  }

  protected test(p: Provider): void {
    this.testing.set(p.code);
    this.bo.post<{ ok: boolean; error: string | null; milliseconds: number }>(`ai/providers/${p.code}/test`).subscribe({
      next: (r) => {
        this.results.update((x) => ({ ...x, [p.code]: r }));
        this.testing.set(null);
      },
      error: (e) => {
        this.testing.set(null);
        this.notify.error(e);
      }
    });
  }
}
