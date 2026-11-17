import { describe, expect, it } from 'vitest';
import { parseAmount } from './cost-amount';

describe('parseAmount', () => {
  it.each([['0', 0], ['0.01', 0.01], ['12.5', 12.5], ['1000', 1000], [' 0.25 ', 0.25]])('accepts %j', (text, amount) => {
    expect(parseAmount(text)).toBe(amount);
  });

  it.each(['-1', '0.001', '1000.01', 'abc', '', '1,5'])('rejects %j', text => {
    expect(parseAmount(text)).toBeNull();
  });
});
