import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError, apiFetch } from "../api/client";
import type { Card, CardListResponse } from "../api/types";
import { CardTile } from "../components/CardTile";
import { ErrorState } from "../components/ErrorState";
import { CardGridSkeleton } from "../components/Skeleton";
import { readWalletScroll, saveWalletScroll } from "../lib/scroll";

type CardsState =
  | { status: "loading" }
  | { status: "ready"; cards: Card[] }
  | { status: "error"; message: string };

function messageOf(error: unknown): string {
  return error instanceof ApiError
    ? error.message
    : "Your cards could not be loaded. Try again.";
}

export function WalletPage() {
  const [state, setState] = useState<CardsState>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);
  const restoredRef = useRef(false);
  const canSaveRef = useRef(false);
  const frameRef = useRef<number | null>(null);

  useEffect(() => {
    let active = true;
    setState({ status: "loading" });
    apiFetch<CardListResponse>("/api/v1/me/cards")
      .then((response) => {
        if (active) setState({ status: "ready", cards: response.items ?? [] });
      })
      .catch((error: unknown) => {
        if (!active) return;
        // The single client already cleared the session and redirected on 401.
        if (error instanceof ApiError && error.status === 401) return;
        setState({ status: "error", message: messageOf(error) });
      });
    return () => {
      active = false;
    };
  }, [attempt]);

  // Restore once, after the grid has rendered, so the target offset is not
  // clipped by a page that is still shorter than the saved position. Saving is
  // disabled until then: mounting this page at scroll 0 (the user scrolled the
  // detail page back to the top to reach the Back link) fires a scroll event
  // that would otherwise overwrite the saved offset with 0.
  useEffect(() => {
    if (state.status !== "ready" || restoredRef.current) return;
    restoredRef.current = true;
    const offset = readWalletScroll();
    if (offset === null) {
      canSaveRef.current = true;
      return;
    }
    const frame = window.requestAnimationFrame(() => {
      window.scrollTo({ top: offset, left: 0, behavior: "auto" });
      window.requestAnimationFrame(() => {
        canSaveRef.current = true;
      });
    });
    return () => window.cancelAnimationFrame(frame);
  }, [state.status]);

  // Persist while scrolling (rAF-throttled) and again when a card is opened,
  // because the click unmounts this page before the next scroll event.
  useEffect(() => {
    const onScroll = () => {
      if (!canSaveRef.current) return;
      if (frameRef.current !== null) return;
      frameRef.current = window.requestAnimationFrame(() => {
        frameRef.current = null;
        saveWalletScroll(window.scrollY);
      });
    };
    window.addEventListener("scroll", onScroll, { passive: true });
    return () => {
      window.removeEventListener("scroll", onScroll);
      if (frameRef.current !== null) {
        window.cancelAnimationFrame(frameRef.current);
        frameRef.current = null;
      }
    };
  }, []);

  const persistScroll = useCallback(() => {
    if (!canSaveRef.current) return;
    saveWalletScroll(window.scrollY);
  }, []);

  const retry = useCallback(() => setAttempt((value) => value + 1), []);

  return (
    <div>
      <div className="mb-5">
        <h1 className="text-2xl font-bold tracking-tight text-slate-900">Wallet</h1>
        <p className="mt-1 text-sm text-slate-600">Your cards, ready when you are.</p>
      </div>

      {state.status === "loading" ? <CardGridSkeleton /> : null}

      {state.status === "error" ? <ErrorState message={state.message} onRetry={retry} /> : null}

      {state.status === "ready" && state.cards.length === 0 ? (
        <div className="rounded-xl border border-dashed border-slate-300 bg-white p-8 text-center">
          <p className="font-medium text-slate-800">No cards yet</p>
          <p className="mt-1 text-sm text-slate-600">
            Cards appear here once they are issued to your account.
          </p>
        </div>
      ) : null}

      {state.status === "ready" && state.cards.length > 0 ? (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {state.cards.map((card) => (
            <CardTile key={card.id} card={card} onNavigate={persistScroll} />
          ))}
        </div>
      ) : null}
    </div>
  );
}
