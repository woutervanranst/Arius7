export const MAX_AMOUNT = 1000;

/** Parses the Cost card's EUR amount: 0 to {@link MAX_AMOUNT}, to the cent (the server enforces the same). */
export function parseAmount(text: string): number | null {
  const amount = /^\d+(\.\d{1,2})?$/.test(text.trim()) ? Number(text) : NaN;
  return amount >= 0 && amount <= MAX_AMOUNT ? amount : null;
}
