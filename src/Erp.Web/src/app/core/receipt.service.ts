import { Injectable, inject, signal } from '@angular/core';
import { Observable, catchError, of, shareReplay, tap } from 'rxjs';
import { ApiService } from './api.service';
import { I18nService } from './i18n/i18n.service';
import { Company } from './models';

/** Paper on the printer attached to this device: 58 or 80 mm thermal roll, or a normal A4 printer. */
export type PaperWidth = '58' | '80' | 'a4';

export interface PrinterSettings {
  paper: PaperWidth;
  /** Print the till receipt as soon as a sale is paid. */
  autoPrint: boolean;
}

export interface ReceiptLine {
  text: string;
  sub?: string;
  amount: number;
}

export interface ReceiptTotal {
  label: string;
  amount: number;
  bold?: boolean;
}

/** A receipt in the shop's own words; the service adds the shop header and footer and lays it out for the paper. */
export interface ReceiptDoc {
  title: string;
  number?: string;
  meta?: string[];
  lines: ReceiptLine[];
  totals: ReceiptTotal[];
  note?: string;
  /** Used when the company has no footer of its own. */
  footer?: string;
}

const SETTINGS_KEY = 'erp.printer';
const COMPANY_KEY = 'erp.company';

const escape = (s: string | null | undefined) => (s ?? '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!);
const money = (v: number) => v.toFixed(2);

function read<T>(key: string): T | null {
  try {
    const raw = localStorage.getItem(key);
    return raw ? (JSON.parse(raw) as T) : null;
  } catch {
    return null;
  }
}

function write(key: string, value: unknown): void {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    /* storage blocked: settings last for this visit only */
  }
}

/**
 * Prints receipts through the browser on the printer of this device (thermal 58/80 mm or A4). The shop name, address,
 * CR number and footer come from the company settings and are kept for printing without a connection.
 * Chrome started with --kiosk-printing prints straight to the default printer without the dialog.
 */
@Injectable({ providedIn: 'root' })
export class ReceiptService {
  private readonly api = inject(ApiService);
  private readonly i18n = inject(I18nService);
  private company$: Observable<Company | null> | null = null;

  readonly settings = signal<PrinterSettings>({ paper: '80', autoPrint: false, ...read<PrinterSettings>(SETTINGS_KEY) });

  constructor() {
    // Fetch the shop header now, so a receipt printed after the connection drops still has it.
    this.company().subscribe();
  }

  saveSettings(settings: PrinterSettings): void {
    this.settings.set(settings);
    write(SETTINGS_KEY, settings);
  }

  /** Call after the company settings change so the next receipt shows them. */
  refreshCompany(): void {
    this.company$ = null;
  }

  print(doc: ReceiptDoc, paper: PaperWidth = this.settings().paper): void {
    this.company().subscribe((c) => this.printHtml(this.render(doc, c, paper)));
  }

  /** A full A4 page (job card, delivery note) with the shop header on top. */
  printPage(title: string, body: string): void {
    this.company().subscribe((c) => this.printHtml(this.page(title, this.header(c) + body, 'a4')));
  }

  render(doc: ReceiptDoc, company: Company | null, paper: PaperWidth): string {
    const t = (k: Parameters<I18nService['t']>[0]) => this.i18n.t(k);
    const ar = this.i18n.isArabic();
    const lines = doc.lines
      .map((l) => `<tr><td>${escape(l.text)}${l.sub ? `<br><small><bdi dir="auto">${escape(l.sub)}</bdi></small>` : ''}</td><td class="n">${money(l.amount)}</td></tr>`)
      .join('');
    const totals = doc.totals
      .map((x) => `<tr${x.bold ? ' class="b"' : ''}><td>${escape(x.label)}</td><td class="n">${money(x.amount)}</td></tr>`)
      .join('');
    const footer = (ar ? company?.receiptFooterAr || company?.receiptFooterEn : company?.receiptFooterEn || company?.receiptFooterAr) || doc.footer || t('pos.thanks');
    const body = `${this.header(company)}
      <h2>${escape(doc.title)}</h2>${doc.number ? `<p class="no"><bdi dir="ltr">${escape(doc.number)}</bdi></p>` : ''}
      ${(doc.meta ?? []).map((m) => `<p><bdi dir="auto">${escape(m)}</bdi></p>`).join('')}
      <table class="lines">${lines}</table>
      <table class="totals">${totals}</table>
      ${doc.note ? `<p class="note">${escape(doc.note)}</p>` : ''}
      <p class="foot">${escape(footer).replace(/\n/g, '<br>')}</p>`;
    return this.page(doc.number ?? doc.title, body, paper);
  }

  private header(c: Company | null): string {
    if (!c) return '';
    const ar = this.i18n.isArabic();
    const address = ar ? c.addressAr || c.addressEn : c.addressEn || c.addressAr;
    const other = ar ? c.nameEn : c.nameAr;
    return `<header><h1>${escape(ar ? c.nameAr : c.nameEn)}</h1>${other && other !== (ar ? c.nameAr : c.nameEn) ? `<div>${escape(other)}</div>` : ''}
      ${address ? `<div>${escape(address)}</div>` : ''}
      ${c.phone ? `<div class="ltr">${escape(c.phone)}</div>` : ''}
      ${c.crNo ? `<div>${escape(this.i18n.t('company.crNo'))}: <span class="ltr">${escape(c.crNo)}</span></div>` : ''}</header>`;
  }

  private page(title: string, body: string, paper: PaperWidth): string {
    const thermal = paper !== 'a4';
    const width = paper === '58' ? '48mm' : '72mm';
    const css = thermal
      ? `@page{size:${paper}mm auto;margin:0}body{width:${width};margin:2mm auto;font-size:${paper === '58' ? 11 : 12}px}`
      : `@page{size:A4;margin:12mm}body{max-width:180mm;margin:0 auto;font-size:13px}`;
    return `<!doctype html><html lang="${this.i18n.lang()}" dir="${this.i18n.dir()}"><head><meta charset="utf-8"><title>${escape(title)}</title><style>
      ${css}
      body{font-family:'IBM Plex Sans Arabic',Tahoma,Arial,sans-serif;color:#000;line-height:1.35}
      header{text-align:center;border-bottom:1px dashed #000;padding-bottom:4px;margin-bottom:4px}
      header h1{font-size:1.35em;margin:0}h2{font-size:1.1em;text-align:center;margin:4px 0}p{margin:2px 0;text-align:center}
      table{width:100%;border-collapse:collapse}td,th{padding:2px 0;vertical-align:top;text-align:start}
      .lines td{border-bottom:1px dotted #999}.totals{margin-top:4px;border-top:1px solid #000}.n{text-align:end;white-space:nowrap;padding-inline-start:6px}
      .b td{font-weight:700;font-size:1.15em}.note{font-size:.9em;text-align:start}.foot{margin-top:8px;border-top:1px dashed #000;padding-top:4px}
      .ltr{direction:ltr;unicode-bidi:isolate}.no{font-weight:700;font-size:1.05em}small{color:#333}
      </style></head><body>${body}</body></html>`;
  }

  private company(): Observable<Company | null> {
    this.company$ ??= this.api.company().pipe(
      tap((c) => write(COMPANY_KEY, c)),
      // Offline: the copy kept from the last time keeps the header on the receipt; try the server again next time.
      catchError(() => {
        this.company$ = null;
        return of(read<Company>(COMPANY_KEY));
      }),
      shareReplay(1)
    );
    return this.company$;
  }

  /** Prints from a hidden frame, so no window opens and pop-up blockers do not interfere. */
  private printHtml(html: string): void {
    const frame = document.createElement('iframe');
    frame.setAttribute('aria-hidden', 'true');
    frame.style.cssText = 'position:fixed;width:0;height:0;border:0;inset-inline-start:-10000px;top:0';
    frame.onload = () => {
      const w = frame.contentWindow;
      if (!w) return;
      w.onafterprint = () => setTimeout(() => frame.remove(), 500);
      setTimeout(() => {
        w.focus();
        w.print();
      }, 100);
      setTimeout(() => frame.remove(), 120_000);
    };
    frame.srcdoc = html;
    document.body.appendChild(frame);
  }
}
