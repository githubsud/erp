/** Date helpers that never shift days across time zones (the API uses plain yyyy-MM-dd dates). */

export function todayIso(): string {
  return toIso(new Date());
}

export function toIso(d: Date): string {
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, '0');
  const day = String(d.getDate()).padStart(2, '0');
  return `${y}-${m}-${day}`;
}

export function monthStartIso(year: number, month: number): string {
  return `${year}-${String(month).padStart(2, '0')}-01`;
}

export function monthEndIso(year: number, month: number): string {
  return toIso(new Date(year, month, 0));
}

export function daysBetween(fromIso: string, toIsoDate: string): number {
  const a = Date.UTC(+fromIso.slice(0, 4), +fromIso.slice(5, 7) - 1, +fromIso.slice(8, 10));
  const b = Date.UTC(+toIsoDate.slice(0, 4), +toIsoDate.slice(5, 7) - 1, +toIsoDate.slice(8, 10));
  return Math.round((b - a) / 86_400_000);
}

/** dd/MM/yyyy, the usual format on Qatari documents. */
export function displayDate(iso: string | null | undefined): string {
  if (!iso) return '';
  return `${iso.slice(8, 10)}/${iso.slice(5, 7)}/${iso.slice(0, 4)}`;
}

/** Empty strings from forms become null for the API. */
export function blankToNull<T extends Record<string, unknown>>(value: T): T {
  const copy: Record<string, unknown> = { ...value };
  for (const k of Object.keys(copy)) if (copy[k] === '') copy[k] = null;
  return copy as T;
}
