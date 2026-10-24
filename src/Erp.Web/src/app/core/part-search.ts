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
const yearOf = (word: string) => (/^\d{4}$/.test(word) && +word >= 1950 && +word <= 2100 ? +word : null);
const carHas = (f: Fitment, w: string) => f.make.toLowerCase().includes(w) || f.model.toLowerCase().includes(w) || !!f.engine?.toLowerCase().includes(w);

/**
 * Every word must match something: the code, a name, the brand, a barcode, another part number, or a car the part fits
 * ("corolla 2015 filter": a filter that fits a 2015 Corolla). A model year belongs to one car: the part must fit, in that year,
 * a car that also answers the other car words ("corolla 2021" is not a part for 2014-2019 Corollas that also fits 2021 Camrys).
 */
export function matchesSearch(item: Searchable, term: string): boolean {
  const words = term.trim().split(/\s+/).filter(Boolean);
  const fits = item.fits ?? [];
  // Words that name one of this part's cars (the others, like "filter", are about the part itself).
  const carWords = words.filter((w) => yearOf(w) === null && fits.some((f) => carHas(f, w.toLowerCase()))).map((w) => w.toLowerCase());
  return words.every((word) => {
    const w = word.toLowerCase();
    const key = partKey(word);
    const year = yearOf(word);
    return (
      item.sku.toLowerCase().includes(w) ||
      item.nameEn.toLowerCase().includes(w) ||
      item.nameAr.includes(word) ||
      item.barcode === word ||
      !!item.brand?.toLowerCase().includes(w) ||
      !!item.units?.some((u) => u.barcode === word) ||
      (key !== '' && !!item.partKeys?.some((k) => k.includes(key))) ||
      (year !== null && fits.some((f) => fitsYear(f, year) && carWords.every((c) => carHas(f, c)))) ||
      fits.some((f) => carHas(f, w))
    );
  });
}
