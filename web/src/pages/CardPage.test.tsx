import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, apiFetch } from "../api/client";
import type { Card, Transaction, TransactionListResponse } from "../api/types";
import { loadCardArt } from "../lib/art";
import { CardPage } from "./CardPage";

vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return { ...actual, apiFetch: vi.fn() };
});

vi.mock("../lib/art", () => ({
  loadCardArt: vi.fn(),
  clearCardArtCache: vi.fn(),
}));

const apiFetchMock = vi.mocked(apiFetch);
const loadArtMock = vi.mocked(loadCardArt);

const CARD_ID = "c1000000-0000-4000-8000-000000000001";
const card: Card = {
  id: CARD_ID,
  brand: "visa",
  last4: "4242",
  expiry_month: 9,
  expiry_year: 2028,
  status: "active",
  currency: "AUD",
  display_order: 0,
  art_url: `http://localhost:8082/api/v1/cards/${CARD_ID}/art`,
};

const FIRST_PAGE = `/api/v1/me/cards/${CARD_ID}/transactions?limit=10`;
const dayOne = new Date(2026, 9, 31, 12, 0);
const dayTwo = new Date(2026, 9, 30, 12, 0);

let nextId = 0;

function makeTransaction(overrides: Partial<Transaction> = {}): Transaction {
  nextId += 1;
  return {
    id: `t${nextId}`,
    card_id: CARD_ID,
    amount_minor: 450,
    currency: "AUD",
    direction: "debit",
    merchant: `Merchant ${nextId}`,
    status: "posted",
    occurred_at: dayOne.toISOString(),
    ...overrides,
  };
}

function page(items: Transaction[], nextCursor: string | null): TransactionListResponse {
  return { items, next_cursor: nextCursor };
}

function mockRoutes(
  transactions: (path: string) => Promise<TransactionListResponse>,
  cards: Card[] = [card],
) {
  apiFetchMock.mockImplementation(((path: string) => {
    if (path === "/api/v1/me/cards") return Promise.resolve({ items: cards });
    if (path.startsWith(`/api/v1/me/cards/${CARD_ID}/transactions`)) {
      return transactions(path);
    }
    return Promise.reject(new Error(`unexpected path ${path}`));
  }) as never);
}

function renderCardPage() {
  return render(
    <MemoryRouter initialEntries={[`/wallet/cards/${CARD_ID}`]}>
      <Routes>
        <Route path="/wallet/cards/:cardId" element={<CardPage />} />
        <Route path="/wallet" element={<div>Wallet destination</div>} />
        <Route path="/login" element={<div>Login destination</div>} />
      </Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  apiFetchMock.mockReset();
  loadArtMock.mockReset();
  loadArtMock.mockResolvedValue("blob:card-art");
  nextId = 0;
});

describe("CardPage transactions", () => {
  it("requests the first page with limit=10 and renders date groups, merchant, time and signed amount", async () => {
    const items = [
      makeTransaction({
        amount_minor: 450,
        direction: "debit",
        merchant: "Dan Murphy's",
        occurred_at: new Date(2026, 9, 31, 21, 0).toISOString(),
      }),
      makeTransaction({
        amount_minor: 500,
        direction: "credit",
        merchant: "Qantas",
        occurred_at: new Date(2026, 9, 31, 9, 5).toISOString(),
      }),
      ...Array.from({ length: 8 }, (_, index) =>
        makeTransaction({
          merchant: `Extra ${index}`,
          amount_minor: 1000 + index,
          occurred_at: dayTwo.toISOString(),
        }),
      ),
    ];
    mockRoutes(() => Promise.resolve(page(items, "next-page")));

    renderCardPage();

    expect(
      await screen.findByRole("heading", { name: "Transactions" }),
    ).toBeInTheDocument();
    expect(await screen.findByText("Dan Murphy's")).toBeInTheDocument();
    expect(apiFetchMock).toHaveBeenCalledWith(FIRST_PAGE);

    const dateHeadings = screen.getAllByRole("heading", { level: 3 });
    expect(dateHeadings.map((heading) => heading.textContent)).toEqual([
      "31 Oct 2026",
      "30 Oct 2026",
    ]);
    expect(screen.getByText("Dan Murphy's")).toBeInTheDocument();
    expect(screen.getByText("21:00")).toBeInTheDocument();
    expect(screen.getByText("-A$4.50")).toBeInTheDocument();
    expect(screen.getByText("+A$5.00")).toBeInTheDocument();
    expect(screen.getAllByRole("listitem")).toHaveLength(10);
  });

  it("shows Pending only on pending rows", async () => {
    const pending = makeTransaction({ merchant: "Bp", status: "pending" });
    const posted = makeTransaction({ merchant: "Coles", status: "posted" });
    mockRoutes(() => Promise.resolve(page([pending, posted], null)));

    renderCardPage();

    const pendingRow = (await screen.findByText("Bp")).closest("li");
    const postedRow = screen.getByText("Coles").closest("li");
    expect(pendingRow).not.toBeNull();
    expect(postedRow).not.toBeNull();
    expect(within(pendingRow as HTMLElement).getByText("Pending")).toBeInTheDocument();
    expect(within(postedRow as HTMLElement).queryByText("Pending")).toBeNull();
    expect(screen.getAllByText("Pending")).toHaveLength(1);
  });

  it("loads the next page with an encoded cursor, deduplicates by id and hides the button at the end", async () => {
    const user = userEvent.setup();
    const firstPage = Array.from({ length: 10 }, (_, index) =>
      makeTransaction({ id: `a${index + 1}`, merchant: `Merchant a${index + 1}` }),
    );
    const secondPage = [
      firstPage[9],
      makeTransaction({ id: "b1", merchant: "Merchant b1" }),
      makeTransaction({ id: "b2", merchant: "Merchant b2" }),
      makeTransaction({ id: "b3", merchant: "Merchant b3" }),
      makeTransaction({ id: "b4", merchant: "Merchant b4" }),
    ];
    mockRoutes((path) =>
      path === FIRST_PAGE
        ? Promise.resolve(page(firstPage, "cursor/one"))
        : Promise.resolve(page(secondPage, null)),
    );

    renderCardPage();

    await waitFor(() => expect(screen.getAllByRole("listitem")).toHaveLength(10));
    await user.click(screen.getByRole("button", { name: "Load more" }));

    await waitFor(() => expect(screen.getAllByRole("listitem")).toHaveLength(14));
    expect(
      apiFetchMock,
    ).toHaveBeenCalledWith(`${FIRST_PAGE}&cursor=cursor%2Fone`);
    expect(screen.getAllByText("Merchant a10")).toHaveLength(1);
    expect(screen.getByText("Merchant b4")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Load more" })).toBeNull();
  });

  it("does not render Load more when next_cursor is null", async () => {
    mockRoutes(() => Promise.resolve(page([makeTransaction()], null)));

    renderCardPage();

    await waitFor(() => expect(screen.getAllByRole("listitem")).toHaveLength(1));
    expect(screen.queryByRole("button", { name: "Load more" })).toBeNull();
  });

  it("shows the required empty-state copy", async () => {
    mockRoutes(() => Promise.resolve(page([], null)));

    renderCardPage();

    expect(await screen.findByText("No transactions yet.")).toBeInTheDocument();
  });

  it("shows an error with Retry for a failed first page, then loads it", async () => {
    const user = userEvent.setup();
    let calls = 0;
    mockRoutes(() => {
      calls += 1;
      if (calls === 1) {
        return Promise.reject(
          new ApiError(503, "UPSTREAM_UNAVAILABLE", "The service is temporarily unavailable."),
        );
      }
      return Promise.resolve(page([makeTransaction({ merchant: "Recovered" })], null));
    });

    renderCardPage();

    expect(
      await screen.findByText("The service is temporarily unavailable."),
    ).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Retry" }));

    expect(await screen.findByText("Recovered")).toBeInTheDocument();
    expect(screen.getAllByRole("listitem")).toHaveLength(1);
  });

  it("keeps loaded rows when Load more fails and retries the same cursor", async () => {
    const user = userEvent.setup();
    const firstPage = Array.from({ length: 10 }, (_, index) =>
      makeTransaction({ merchant: `First ${index}` }),
    );
    let loadMoreCalls = 0;
    mockRoutes((path) => {
      if (path === FIRST_PAGE) return Promise.resolve(page(firstPage, "cursor-two"));
      loadMoreCalls += 1;
      if (loadMoreCalls === 1) {
        return Promise.reject(
          new ApiError(0, "NETWORK_ERROR", "The service could not be reached."),
        );
      }
      return Promise.resolve(
        page(
          [
            makeTransaction({ merchant: "Appended 1" }),
            makeTransaction({ merchant: "Appended 2" }),
          ],
          null,
        ),
      );
    });

    renderCardPage();

    await waitFor(() => expect(screen.getAllByRole("listitem")).toHaveLength(10));
    await user.click(screen.getByRole("button", { name: "Load more" }));

    expect(
      await screen.findByText(/Could not load more transactions/),
    ).toBeInTheDocument();
    expect(screen.getAllByRole("listitem")).toHaveLength(10);

    await user.click(screen.getByRole("button", { name: "Retry loading more" }));

    await waitFor(() => expect(screen.getAllByRole("listitem")).toHaveLength(12));
    expect(screen.getByText("Appended 1")).toBeInTheDocument();
    expect(
      apiFetchMock.mock.calls.filter(([path]) =>
        path.startsWith(`${FIRST_PAGE}&cursor=cursor-two`),
      ),
    ).toHaveLength(2);
  });

  it("shows Card not found for a card outside the account and skips transactions", async () => {
    mockRoutes(() => Promise.resolve(page([], null)), []);

    renderCardPage();

    expect(await screen.findByText("Card not found")).toBeInTheDocument();
    expect(apiFetchMock).toHaveBeenCalledTimes(1);
    expect(apiFetchMock).toHaveBeenCalledWith("/api/v1/me/cards");
  });

  it("does not render an error block for 401 (the request layer redirects)", async () => {
    mockRoutes(() =>
      Promise.reject(new ApiError(401, "UNAUTHENTICATED", "Your session is no longer valid.")),
    );

    renderCardPage();

    await waitFor(() => expect(apiFetchMock).toHaveBeenCalledTimes(2));
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    expect(screen.queryByText("No transactions yet.")).not.toBeInTheDocument();
  });
});
