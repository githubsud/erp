import { stockLevel } from './stock-alert';

describe('stockLevel', () => {
  it('is out when there is not enough for the sale or nothing left', () => {
    expect(stockLevel(3, 8, 4)).toBe('out');
    expect(stockLevel(0, null)).toBe('out');
    expect(stockLevel(-2, 5)).toBe('out');
  });

  it('is low when what is left is at or below the reorder level', () => {
    expect(stockLevel(10, 8, 2)).toBe('low');
    expect(stockLevel(10, 8, 3)).toBe('low');
    expect(stockLevel(4, 8)).toBe('low');
    expect(stockLevel(4, 8, 4)).toBe('low');
  });

  it('is ok above the reorder level, and without one', () => {
    expect(stockLevel(10, 8, 1)).toBe('ok');
    expect(stockLevel(1, null, 1)).toBe('ok');
    expect(stockLevel(5, undefined)).toBe('ok');
  });
});
