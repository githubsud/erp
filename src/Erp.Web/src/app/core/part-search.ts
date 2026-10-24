/** A car a spare part fits; years left out are open-ended. */
export interface Fitment {
  make: string;
  model: string;
  yearFrom: number | null;
  yearTo: number | null;
  engine: string | null;
}

/** What the till's offline search looks at (same rules as the server's item search). */
export interface Searchable {
  sku: string;
  nameEn: string;
  nameAr: string;
  barcode?: string | null;
  brand?: string | null;
  partKeys?: string[] | null;
  fits?: Fitment[] | null;
  units?: { barcode: string | null }[] | null;
}

/** Part numbers compare in upper case, letters and digits only: "04152-YZZA1" = "04152yzza1". */
export const partKey = (s: string) => s.replace(/[^\p{L}\p{N}]/gu, '').toUpperCase();

/** "2014–2019", "2012+", "–2010", or nothing for every year. */
export function yearsText(f: Pick<Fitment, 'yearFrom' | 'yearTo'>): string {
  if (f.yearFrom && f.yearTo) return f.yearFrom === f.yearTo ? `${f.yearFrom}` : `${f.yearFrom}–${f.yearTo}`;
  if (f.yearFrom) return `${f.yearFrom}+`;
  if (f.yearTo) return `–${f.yearTo}`;
  return '';
}

const fitsYear = (f: Fitment, year: number) => (f.yearFrom == null || f.yearFrom <= year) && (f.yearTo == null || f.yearTo >= year);

/**
 * Every word must match something: the code, a name, the brand, a barcode, another part number, or a car the part fits
 * ("corolla 2015 filter": a filter that fits a 2015 Corolla).
 */
export function matchesSearch(item: Searchable, term: string): boolean {
  return term
    .trim()
    .split(/\s+/)
    .filter(Boolean)
    .every((word) => {
      const w = word.toLowerCase();
      const key = partKey(word);
      const year = /^\d{4}$/.test(word) && +word >= 1950 && +word <= 2100 ? +word : null;
      return (
        item.sku.toLowerCase().includes(w) ||
        item.nameEn.toLowerCase().includes(w) ||
        item.nameAr.includes(word) ||
        item.barcode === word ||
        !!item.brand?.toLowerCase().includes(w) ||
        !!item.units?.some((u) => u.barcode === word) ||
        (key !== '' && !!item.partKeys?.some((k) => k.includes(key))) ||
        !!item.fits?.some(
          (f) =>
            f.make.toLowerCase().includes(w) ||
            f.model.toLowerCase().includes(w) ||
            !!f.engine?.toLowerCase().includes(w) ||
            (year !== null && fitsYear(f, year))
        )
      );
    });
}
