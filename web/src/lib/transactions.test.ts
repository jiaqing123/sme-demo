import { describe, expect, it } from "vitest";
import type { Transaction } from "../api/types";
import { appendUniqueTransactions, groupTransactionsByDate } from "./transactions";

function tx(overrides: Partial<Transaction> & Pick<Transaction, "id">): Transaction {
  const { id, ...rest } = overrides;
  return {
    id,
    card_id: "c1",
    amount_minor: 450,
    currency: "AUD",
    direction: "debit",
    merchant: `Merchant ${id}`,
    status: "posted",
    occurred_at: new Date(2026, 9, 31, 12, 0).toISOString(),
    ...rest,
  };
}

describe("groupTransactionsByDate", () => {
  it("groups by local calendar day in first-seen order and keeps row order", () => {
    const late = new Date(2026, 9, 31, 23, 30);
    const early = new Date(2026, 10, 1, 0, 30);
    const items = [
      tx({ id: "a", occurred_at: late.toISOString() }),
      tx({ id: "b", occurred_at: new Date(2026, 9, 31, 9, 0).toISOString() }),
      tx({ id: "c", occurred_at: early.toISOString() }),
    ];

    const groups = groupTransactionsByDate(items);

    expect(groups.map((group) => group.key)).toEqual(["2026-10-31", "2026-11-01"]);
    expect(groups.map((group) => group.label)).toEqual(["31 Oct 2026", "01 Nov 2026"]);
    expect(groups[0].transactions.map((item) => item.id)).toEqual(["a", "b"]);
    expect(groups[1].transactions.map((item) => item.id)).toEqual(["c"]);
  });

  it("does not sort rows or groups: API order wins", () => {
    const items = [
      tx({ id: "later", occurred_at: new Date(2026, 8, 2, 12, 0).toISOString() }),
      tx({ id: "earlier", occurred_at: new Date(2026, 8, 1, 12, 0).toISOString() }),
    ];

    const groups = groupTransactionsByDate(items);

    expect(groups.map((group) => group.key)).toEqual(["2026-09-02", "2026-09-01"]);
  });

  it("returns no groups for an empty list", () => {
    expect(groupTransactionsByDate([])).toEqual([]);
  });
});

describe("appendUniqueTransactions", () => {
  it("appends new rows and drops duplicate ids", () => {
    const existing = [tx({ id: "a" }), tx({ id: "b" })];
    const incoming = [tx({ id: "b" }), tx({ id: "c" })];

    const result = appendUniqueTransactions(existing, incoming);

    expect(result.map((item) => item.id)).toEqual(["a", "b", "c"]);
  });

  it("returns the original array when the page is entirely duplicates", () => {
    const existing = [tx({ id: "a" })];
    const result = appendUniqueTransactions(existing, [tx({ id: "a" })]);

    expect(result).toBe(existing);
  });
});
