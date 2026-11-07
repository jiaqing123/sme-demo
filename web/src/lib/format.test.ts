import { describe, expect, it } from "vitest";
import {
  formatDate,
  formatExpiry,
  formatMaskedNumber,
  formatMoney,
  formatStatus,
  formatTime,
  localDateKey,
} from "./format";

describe("formatMoney", () => {
  it("formats debit with a minus and the A$ symbol", () => {
    expect(formatMoney(1240, "AUD", "debit")).toBe("-A$12.40");
  });

  it("formats credit with a plus", () => {
    expect(formatMoney(500, "AUD", "credit")).toBe("+A$5.00");
  });

  it("formats without a direction when none is supplied", () => {
    expect(formatMoney(1240, "AUD")).toBe("A$12.40");
  });

  it("groups thousands", () => {
    expect(formatMoney(123450, "AUD", "debit")).toBe("-A$1,234.50");
  });

  it("formats zero", () => {
    expect(formatMoney(0, "AUD", "credit")).toBe("+A$0.00");
  });

  it("leaves other currencies to Intl", () => {
    // en-AU renders USD with its code; ICU may use a non-breaking space.
    expect(formatMoney(1234, "USD")).toMatch(/USD\s12\.34/);
  });
});

describe("dates and times", () => {
  it("formats DD MMM YYYY", () => {
    expect(formatDate(new Date(2026, 9, 31, 12, 0))).toBe("31 Oct 2026");
  });

  it("formats HH:mm", () => {
    expect(formatTime(new Date(2026, 9, 31, 14, 5))).toBe("14:05");
    expect(formatTime(new Date(2026, 9, 31, 9, 0))).toBe("09:00");
  });

  it("uses the local calendar day for grouping, not the UTC day", () => {
    // 23:30 local on 31 October is already 1 November in some UTC offsets.
    const late = new Date(2026, 9, 31, 23, 30);
    expect(localDateKey(late)).toBe("2026-10-31");
    const early = new Date(2026, 0, 1, 0, 30);
    expect(localDateKey(early)).toBe("2026-01-01");
  });

  it("formats expiry as MM/YY", () => {
    expect(formatExpiry(9, 2028)).toBe("09/28");
    expect(formatExpiry(11, 2026)).toBe("11/26");
  });
});

describe("card labels", () => {
  it("capitalises status", () => {
    expect(formatStatus("active")).toBe("Active");
    expect(formatStatus("blocked")).toBe("Blocked");
  });

  it("masks the card number", () => {
    expect(formatMaskedNumber("4242")).toBe("•••• 4242");
  });
});
