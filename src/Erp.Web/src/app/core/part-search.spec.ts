import { matchesSearch, partKey, Searchable, yearsText } from './part-search';

describe('part search', () => {
  const filter: Searchable = {
    sku: 'OF-DENSO-1', nameEn: 'Oil filter', nameAr: 'فلتر زيت', brand: 'Denso', partKeys: ['04152YZZA1'],
    fits: [
      { make: 'Toyota', model: 'Corolla', yearFrom: 2014, yearTo: 2019, engine: '1.6' },
      { make: 'Toyota', model: 'Camry', yearFrom: 2012, yearTo: null, engine: null }
    ]
  };

  it('finds a part by any spelling of its number, its brand and its name', () => {
    expect(partKey('04152-yzza1')).toBe('04152YZZA1');
    expect(matchesSearch(filter, '04152-YZZA1')).toBe(true);
    expect(matchesSearch(filter, '04152yzza1')).toBe(true);
    expect(matchesSearch(filter, 'denso')).toBe(true);
    expect(matchesSearch(filter, 'فلتر')).toBe(true);
    expect(matchesSearch(filter, 'bosch')).toBe(false);
  });

  it('finds a part by car and model year, open-ended years fitting every later model', () => {
    expect(matchesSearch(filter, 'corolla 2015')).toBe(true);
    expect(matchesSearch(filter, 'camry 2025 filter')).toBe(true); // the Camry fit has no end year
    expect(matchesSearch(filter, 'patrol 2015')).toBe(false);
    // The year must belong to the same car: 2021 fits the Camry, not the Corolla.
    expect(matchesSearch(filter, 'corolla 2021')).toBe(false);
    expect(matchesSearch(filter, 'toyota 2021')).toBe(true);
  });

  it('writes model years briefly', () => {
    expect(yearsText({ yearFrom: 2014, yearTo: 2019 })).toBe('2014–2019');
    expect(yearsText({ yearFrom: 2012, yearTo: null })).toBe('2012+');
    expect(yearsText({ yearFrom: null, yearTo: null })).toBe('');
  });
});
