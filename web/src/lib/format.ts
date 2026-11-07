/**
 * Presentation formatting. English-only, en-AU:
 * - money: `A$12.40`, debit prefixed `-`, credit prefixed `+`
 * - dates: `31 Oct 2026`, times `HH:mm`
 * - date grouping uses the browser's local time zone, not UTC
 */

const moneyFormatters = new Map<string, Intl.NumberFormat>();
const dateFormatter = new Intl.DateTimeFormat("en-AU", {
  day: "2-digit",
  month: "short",
  year: "numeric",
});
const timeFormatter = new Intl.DateTimeFormat("en-AU", {
  hour: "2-digit",
  minute: "2-digit",
  hourCycle: "h23",
});

function moneyFormatter(currency: string): Intl.NumberFormat {
  const key = currency.toUpperCase();
  const existing = moneyFormatters.get(key);
  if (existing) return existing;
  const formatter = new Intl.NumberFormat("en-AU", {
    style: "currency",
    currency: key,
    currencyDisplay: "symbol",
  });
  moneyFormatters.set(key, formatter);
  return formatter;
}

function formatAmount(amountMinor: number, currency: string): string {
  const value = Math.abs(amountMinor) / 100;
  const parts = moneyFormatter(currency).formatToParts(value);
  // Node's ICU renders en-AU AUD as "$12.40"; the UI contract is A$12.40.
  const text = parts
    .map((part) =>
      part.type === "currency" && currency.toUpperCase() === "AUD" ? "A$" : part.value,
    )
    .join("");
  return text;
}

export function formatMoney(
  amountMinor: number,
  currency: string,
  direction?: "debit" | "credit",
): string {
  const text = formatAmount(amountMinor, currency);
  if (direction === "debit") return `-${text}`;
  if (direction === "credit") return `+${text}`;
  return text;
}

export function formatDate(value: string | number | Date): string {
  const date = value instanceof Date ? value : new Date(value);
  const parts = dateFormatter.formatToParts(date);
  const day = parts.find((part) => part.type === "day")?.value ?? "";
  const month = parts.find((part) => part.type === "month")?.value ?? "";
  const year = parts.find((part) => part.type === "year")?.value ?? "";
  return `${day} ${month} ${year}`.trim();
}

export function formatTime(value: string | number | Date): string {
  const date = value instanceof Date ? value : new Date(value);
  const parts = timeFormatter.formatToParts(date);
  const hour = parts.find((part) => part.type === "hour")?.value ?? "00";
  const minute = parts.find((part) => part.type === "minute")?.value ?? "00";
  return `${hour}:${minute}`;
}

/** Grouping key in the browser's local time zone: `YYYY-MM-DD`. */
export function localDateKey(value: string | number | Date): string {
  const date = value instanceof Date ? value : new Date(value);
  const year = String(date.getFullYear()).padStart(4, "0");
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

/** `09/28` for September 2028. Years are shown with two digits. */
export function formatExpiry(month: number, year: number): string {
  const mm = String(month).padStart(2, "0");
  const yy = year >= 100 ? String(year % 100).padStart(2, "0") : String(year).padStart(2, "0");
  return `${mm}/${yy}`;
}

/** `active` -> `Active`; unknown values are left readable. */
export function formatStatus(status: string): string {
  if (!status) return "Unknown";
  return status.charAt(0).toUpperCase() + status.slice(1).toLowerCase();
}

/** `4242` -> `•••• 4242`. */
export function formatMaskedNumber(last4: string): string {
  return `•••• ${last4}`;
}
