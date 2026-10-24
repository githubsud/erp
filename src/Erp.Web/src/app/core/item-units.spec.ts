import { inBoxes, withBoxes } from './item-units';

describe('withBoxes', () => {
  const units = [{ uomCode: 'PCS', factor: 1, isBase: true }, { uomCode: 'BOX10', factor: 10 }];

  it('shows the pieces first, then the boxes they make', () => {
    expect(withBoxes(30, 'PCS', units)).toBe('30 PCS (3 BOX10)');
    expect(withBoxes(37, 'PCS', units)).toBe('37 PCS (3 BOX10 + 7 PCS)');
    expect(withBoxes(7, 'PCS', units)).toBe('7 PCS');
  });
});

describe('inBoxes', () => {
  const units = [{ uomCode: 'PCS', factor: 1, isBase: true }, { uomCode: 'BOX12', factor: 12 }, { uomCode: 'CTN48', factor: 48 }];

  it('shows the biggest unit that fits, then the loose pieces', () => {
    expect(inBoxes(40, 'PCS', units)).toBe('3 BOX12 + 4 PCS');
    expect(inBoxes(58, 'PCS', units)).toBe('1 CTN48 + 10 PCS');
    expect(inBoxes(100, 'PCS', units)).toBe('2 CTN48 + 4 PCS');
    expect(inBoxes(24, 'PCS', units)).toBe('2 BOX12');
  });

  it('shows pieces alone when less than a box, or without boxes', () => {
    expect(inBoxes(7, 'PCS', units)).toBe('7 PCS');
    expect(inBoxes(0, 'PCS', units)).toBe('0 PCS');
    expect(inBoxes(30, 'PCS', [])).toBe('30 PCS');
  });
});
