import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormControl } from '@angular/forms';
import { MatStepperModule } from '@angular/material/stepper';
import { catchError, firstValueFrom, of } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { todayIso } from '../../core/dates';
import { ModuleInfo, ModulesService } from '../../core/modules.service';
import { NotifyService } from '../../core/notify.service';
import { TradeApi } from '../../core/trade.api';
import { SHARED } from '../../shared/shared';
import { BusinessTemplate, TEMPLATES } from './setup-templates';

interface Row {
  sku: string;
  en: FormControl<string>;
  ar: FormControl<string>;
  type: 'Service' | 'Stock';
  price: FormControl<number>;
  cost?: number;
  pick: FormControl<boolean>;
  exists: boolean;
}

/**
 * Quick setup for a new customer: choose the kind of business, keep the modules it needs, load a starter price list and, where
 * it fits, a till and the salon team. Everything goes through the normal APIs and nothing that already exists is touched.
 */
@Component({
  selector: 'app-setup-wizard',
  imports: [...SHARED, MatStepperModule],
  template: `
    <div class="page-header"><h1>{{ 'setup.title' | t }}</h1></div>
    <p class="secondary">{{ 'setup.hint' | t }}</p>
    <mat-stepper [linear]="true" #stepper class="stepper">
      <mat-step [completed]="!!template()" [label]="'setup.step.type' | t">
        <div class="types">
          @for (t of templates; track t.type) {
            <button class="type" [class.on]="template()?.type === t.type" (click)="choose(t); stepper.next()">
              <mat-icon>{{ t.icon }}</mat-icon><span>{{ 'bo.type.' + t.type | t }}</span>
            </button>
          }
        </div>
      </mat-step>

      <mat-step [label]="'setup.step.modules' | t">
        <p class="secondary">{{ 'setup.modulesHint' | t }}</p>
        <div class="modules">
          @for (m of modules(); track m.code) {
            <div class="module">
              <mat-checkbox [checked]="keep().has(m.code)" [disabled]="m.licensed === false" (change)="toggle(m, $event.checked)">{{ m | bi }}</mat-checkbox>
              @if (m.licensed === false) { <span class="chip">{{ 'modules.notLicensed' | t }}</span> }
              @else if (recommended().has(m.code)) { <span class="chip ok">{{ 'setup.recommended' | t }}</span> }
            </div>
          }
        </div>
        <div class="actions"><button mat-button matStepperPrevious>{{ 'app.back' | t }}</button><button mat-flat-button matStepperNext>{{ 'setup.next' | t }}</button></div>
      </mat-step>

      <mat-step [label]="'setup.step.prices' | t">
        <p class="secondary">{{ 'setup.pricesHint' | t }}</p>
        <div class="table-wrap">
          <table class="line-editor">
            <thead><tr><th></th><th>{{ 'setup.code' | t }}</th><th>{{ 'bo.nameAr' | t }}</th><th>{{ 'bo.nameEn' | t }}</th><th>{{ 'setup.kind' | t }}</th><th class="narrow">{{ 'setup.price' | t }}</th></tr></thead>
            <tbody>
              @for (r of rows(); track r.sku) {
                <tr [class.muted]="r.exists">
                  <td><mat-checkbox [formControl]="r.pick" /></td>
                  <td class="ltr">{{ r.sku }} @if (r.exists) { <span class="chip">{{ 'setup.exists' | t }}</span> }</td>
                  <td><mat-form-field><input matInput dir="rtl" [formControl]="r.ar" /></mat-form-field></td>
                  <td><mat-form-field><input matInput dir="ltr" [formControl]="r.en" /></mat-form-field></td>
                  <td>{{ (r.type === 'Service' ? 'setup.service' : 'setup.product') | t }}</td>
                  <td class="narrow"><mat-form-field><input matInput type="number" dir="ltr" [formControl]="r.price" /></mat-form-field></td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        <div class="actions"><button mat-button matStepperPrevious>{{ 'app.back' | t }}</button><button mat-flat-button matStepperNext>{{ 'setup.next' | t }}</button></div>
      </mat-step>

      <mat-step [label]="'setup.step.extras' | t">
        @if (template()?.till) {
          <mat-checkbox [formControl]="createTill">{{ 'setup.till' | t }}</mat-checkbox>
          @if (hasTill()) { <span class="chip">{{ 'setup.tillExists' | t }}</span> }
        }
        @if (template()?.salonStaff) {
          <p>{{ 'setup.staffHint' | t }}</p>
          <div class="form-grid">
            <mat-form-field class="wide"><mat-label>{{ 'setup.staffNames' | t }}</mat-label><input matInput [formControl]="staffNames" placeholder="Grace, Maricel, Sujata" /></mat-form-field>
            <mat-form-field><mat-label>{{ 'sl.commission' | t }} %</mat-label><input matInput type="number" dir="ltr" [formControl]="commission" /></mat-form-field>
          </div>
        }
        @if (!template()?.till && !template()?.salonStaff) { <p class="secondary">{{ 'setup.noExtras' | t }}</p> }
        <div class="actions"><button mat-button matStepperPrevious>{{ 'app.back' | t }}</button><button mat-flat-button matStepperNext>{{ 'setup.next' | t }}</button></div>
      </mat-step>

      <mat-step [label]="'setup.step.finish' | t">
        @if (!done()) {
          <ul class="summary">
            <li>{{ 'setup.sum.modules' | t: { n: keep().size } }}</li>
            <li>{{ 'setup.sum.items' | t: { n: picked() } }}</li>
            @if (template()?.till && createTill.value && !hasTill()) { <li>{{ 'setup.till' | t }}</li> }
            @if (template()?.salonStaff && staffList().length) { <li>{{ 'setup.sum.staff' | t: { n: staffList().length } }}</li> }
          </ul>
          @if (busy()) { <mat-progress-bar mode="indeterminate" /> <p class="secondary">{{ progress() }}</p> }
          <div class="actions"><button mat-button matStepperPrevious [disabled]="busy()">{{ 'app.back' | t }}</button>
            <button mat-flat-button [disabled]="busy() || !template()" (click)="apply()"><mat-icon>rocket_launch</mat-icon> {{ 'setup.apply' | t }}</button></div>
        } @else {
          <h3>{{ 'setup.ready' | t }}</h3>
          <div class="next">
            @for (n of nextSteps(); track n.link) {
              <a mat-stroked-button [routerLink]="n.link"><mat-icon>{{ n.icon }}</mat-icon> {{ n.label | t }}</a>
            }
          </div>
        }
      </mat-step>
    </mat-stepper>
  `,
  styles: `
    .secondary { color: var(--mat-sys-on-surface-variant); }
    .stepper { background: transparent; }
    .types { display: grid; grid-template-columns: repeat(auto-fill, minmax(160px, 1fr)); gap: 12px; margin-block: 8px; }
    .type { display: flex; flex-direction: column; align-items: center; gap: 8px; padding: 18px 8px; border-radius: 14px; cursor: pointer;
      border: 1px solid var(--mat-sys-outline-variant); background: var(--mat-sys-surface-container-low); color: inherit; font: inherit; }
    .type mat-icon { font-size: 34px; width: 34px; height: 34px; color: var(--mat-sys-primary); }
    .type.on, .type:hover { border-color: var(--mat-sys-primary); background: var(--mat-sys-secondary-container); }
    .modules { display: grid; grid-template-columns: repeat(auto-fill, minmax(260px, 1fr)); gap: 4px 12px; }
    .module { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
    .actions { display: flex; gap: 8px; margin-top: 16px; }
    .muted { opacity: 0.55; }
    .summary li { margin-block: 4px; }
    .next { display: flex; flex-wrap: wrap; gap: 8px; }
    .wide { grid-column: 1 / -1; }
  `
})
export class SetupWizardPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly trade = inject(TradeApi);
  private readonly notify = inject(NotifyService);
  private readonly modulesService = inject(ModulesService);
  protected readonly templates = TEMPLATES;
  protected readonly template = signal<BusinessTemplate | null>(null);
  protected readonly modules = signal<ModuleInfo[]>([]);
  protected readonly keep = signal(new Set<string>());
  protected readonly recommended = computed(() => new Set(this.template()?.modules ?? []));
  protected readonly rows = signal<Row[]>([]);
  protected readonly hasTill = signal(false);
  protected readonly createTill = new FormControl(true, { nonNullable: true });
  protected readonly staffNames = new FormControl('', { nonNullable: true });
  protected readonly commission = new FormControl(10, { nonNullable: true });
  protected readonly busy = signal(false);
  protected readonly done = signal(false);
  protected readonly progress = signal('');
  private existingSkus = new Set<string>();
  private readonly version = signal(0);
  protected readonly picked = computed(() => {
    this.version();
    return this.rows().filter((r) => r.pick.value && !r.exists).length;
  });
  protected readonly staffList = computed(() => {
    this.version();
    return this.staffNames.value.split(/[,،\n]+/).map((x) => x.trim()).filter((x) => x);
  });

  ngOnInit(): void {
    this.api.get<ModuleInfo[]>('core/modules').subscribe((m) => this.modules.set(m));
    this.trade.items({ pageSize: 500 }).pipe(catchError(() => of({ items: [] as { sku: string }[] }))).subscribe((r) =>
      (this.existingSkus = new Set(r.items.map((i) => i.sku.toUpperCase()))));
    // Tills can only be listed while the till module is on; otherwise the wizard creates one if the business needs it.
    if (!this.modulesService.disabled().has('pos'))
      this.api.get<{ id: string }[]>('sales/pos/registers').pipe(catchError(() => of([]))).subscribe((r) => this.hasTill.set(r.length > 0));
    this.staffNames.valueChanges.subscribe(() => this.version.update((v) => v + 1));
  }

  protected choose(t: BusinessTemplate): void {
    this.template.set(t);
    const licensed = new Set(this.modules().filter((m) => m.licensed !== false).map((m) => m.code));
    this.keep.set(new Set(t.modules.filter((m) => licensed.has(m))));
    this.rows.set(t.items.map((i) => {
      const exists = this.existingSkus.has(i.sku.toUpperCase());
      const pick = new FormControl(!exists, { nonNullable: true });
      pick.valueChanges.subscribe(() => this.version.update((v) => v + 1));
      return { sku: i.sku, en: new FormControl(i.en, { nonNullable: true }), ar: new FormControl(i.ar, { nonNullable: true }), type: i.type,
        price: new FormControl(i.price, { nonNullable: true }), cost: i.cost, pick, exists };
    }));
    this.createTill.setValue(!this.hasTill());
    this.version.update((v) => v + 1);
  }

  protected toggle(m: ModuleInfo, on: boolean): void {
    this.keep.update((set) => {
      const next = new Set(set);
      if (on) {
        next.add(m.code);
        m.requires.forEach((r) => next.add(r));
      } else {
        next.delete(m.code);
        this.modules().filter((x) => x.requires.includes(m.code)).forEach((x) => next.delete(x.code));
      }
      return next;
    });
  }

  protected async apply(): Promise<void> {
    const t = this.template();
    if (!t) return;
    this.busy.set(true);
    try {
      this.progress.set('…');
      const disabled = this.modules().filter((m) => m.licensed !== false && !this.keep().has(m.code)).map((m) => m.code);
      const list = await firstValueFrom(this.api.put<ModuleInfo[]>('core/modules', { disabled }));
      this.modulesService.set(list);

      const chosen = this.rows().filter((r) => r.pick.value && !r.exists);
      if (chosen.length) {
        const [groups, uoms] = await Promise.all([firstValueFrom(this.trade.groups()), firstValueFrom(this.trade.uoms())]);
        const group = groups.find((g) => g.code === 'GEN')?.id ?? groups[0]?.id;
        const pcs = uoms.find((u) => u.code === 'PCS')?.id ?? uoms[0]?.id;
        for (const [n, r] of chosen.entries()) {
          this.progress.set(`${n + 1} / ${chosen.length} · ${r.sku}`);
          await firstValueFrom(this.trade.saveItem(null, {
            sku: r.sku, nameEn: r.en.value, nameAr: r.ar.value, itemType: r.type, itemGroupId: group, uomId: pcs, salesPrice: Number(r.price.value) || 0,
            purchasePrice: r.cost ?? null, isActive: true
          }));
        }
      }

      if (t.till && this.createTill.value && !this.hasTill()) {
        const [accounts, warehouses] = await Promise.all([firstValueFrom(this.api.accounts()), firstValueFrom(this.trade.warehouses())]);
        await firstValueFrom(this.api.post('sales/pos/registers', {
          code: 'T1', nameEn: 'Till 1', nameAr: 'الصندوق 1', warehouseId: warehouses.find((w) => w.code === 'MAIN')?.id ?? warehouses[0]?.id,
          cashAccountId: accounts.find((a) => a.code === '1110')?.id, cardAccountId: accounts.find((a) => a.code === '1115')?.id
        }));
        this.hasTill.set(true);
      }

      if (t.salonStaff && this.staffList().length) {
        const existing = await firstValueFrom(this.api.get<{ name: string }[]>('salon/staff').pipe(catchError(() => of([]))));
        const names = new Set(existing.map((s) => s.name.toLowerCase()));
        const colours = ['#f8bbd0', '#d1c4e9', '#c8e6c9', '#ffe0b2', '#b3e5fc', '#f0f4c3'];
        for (const [i, name] of this.staffList().entries())
          if (!names.has(name.toLowerCase()))
            await firstValueFrom(this.api.post('salon/staff', { name, commissionPercent: Number(this.commission.value) || 0, color: colours[i % colours.length] }));
      }
      try {
        localStorage.setItem('erp.setup.done', todayIso());
      } catch {
        // Only hides the dashboard hint.
      }
      this.done.set(true);
      this.notify.success('setup.applied');
    } catch (e) {
      this.notify.error(e);
    } finally {
      this.busy.set(false);
    }
  }

  protected nextSteps(): { icon: string; label: string; link: string }[] {
    const t = this.template()?.type;
    const steps = [{ icon: 'badge', label: 'setup.go.employees', link: '/employees' }];
    if (t === 'tailoring') steps.unshift({ icon: 'checkroom', label: 'setup.go.tailoring', link: '/tailoring' });
    if (t === 'salon') steps.unshift({ icon: 'content_cut', label: 'setup.go.salon', link: '/salon' });
    if (this.template()?.till) steps.unshift({ icon: 'point_of_sale', label: 'setup.go.pos', link: '/sales/pos' });
    if (t === 'contracting') steps.unshift({ icon: 'engineering', label: 'setup.go.projects', link: '/sales/projects' });
    steps.push({ icon: 'inventory_2', label: 'setup.go.items', link: '/inventory/items' }, { icon: 'folder_shared', label: 'setup.go.documents', link: '/documents' },
      { icon: 'space_dashboard', label: 'setup.go.dashboard', link: '/dashboard' });
    return steps;
  }
}
