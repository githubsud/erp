import { readFileSync } from 'node:fs';
import { join } from 'node:path';

/** What global-setup created for this run; the tests read it from e2e/.shop.json. */
export interface Shop {
  code: string;
  owner: { email: string; password: string };
  cashier: { email: string; password: string };
  items: { sku: string; nameAr: string; price: number }[];
  employee: { empNo: string; nameEn: string };
}

export const SHOP_FILE = join(__dirname, '..', '.shop.json');

export function shop(): Shop {
  return JSON.parse(readFileSync(SHOP_FILE, 'utf8')) as Shop;
}

/** Small JSON client for seeding through the API (the same calls the app makes). */
export class Api {
  constructor(
    private readonly base: string,
    private token?: string
  ) {}

  static async signIn(base: string, code: string, email: string, password: string): Promise<Api> {
    const api = new Api(base);
    const session = await api.post<{ accessToken: string }>('auth/login', { tenantCode: code, email, password });
    api.token = session.accessToken;
    return api;
  }

  get<T>(path: string): Promise<T> {
    return this.send<T>('GET', path);
  }

  post<T>(path: string, body: unknown, headers: Record<string, string> = {}): Promise<T> {
    return this.send<T>('POST', path, body, headers);
  }

  put<T>(path: string, body: unknown): Promise<T> {
    return this.send<T>('PUT', path, body);
  }

  private async send<T>(method: string, path: string, body?: unknown, headers: Record<string, string> = {}): Promise<T> {
    const response = await fetch(`${this.base}/api/${path}`, {
      method,
      headers: {
        'Content-Type': 'application/json',
        ...(this.token ? { Authorization: `Bearer ${this.token}` } : {}),
        ...headers
      },
      body: body === undefined ? undefined : JSON.stringify(body)
    });
    const text = await response.text();
    if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${text}`);
    return (text ? JSON.parse(text) : undefined) as T;
  }
}

/** A valid Qatari IBAN for a bank code and account number (ISO 13616 check digits). */
export function iban(bank: string, account: string): string {
  const bban = bank + account;
  const numeric = (bban + 'QA00').replace(/[A-Z]/g, (c) => String(c.charCodeAt(0) - 55));
  const remainder = numeric.split('').reduce((r, d) => (r * 10 + Number(d)) % 97, 0);
  return `QA${String(98 - remainder).padStart(2, '0')}${bban}`;
}

/** Today in Qatar as yyyy-MM-dd. */
export function today(): string {
  return new Date(Date.now() + 3 * 3600_000).toISOString().slice(0, 10);
}
