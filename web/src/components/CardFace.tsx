import { useEffect, useState } from "react";
import type { Card } from "../api/types";
import { loadCardArt } from "../lib/art";
import { formatExpiry, formatMaskedNumber, formatStatus } from "../lib/format";

function gradientFor(brand: string): string {
  switch (brand.toLowerCase()) {
    case "visa":
      return "from-[#1a1f71] to-[#3b4cc0]";
    case "mastercard":
      return "from-[#2b2b2b] to-[#5a5a5a]";
    default:
      return "from-slate-700 to-slate-500";
  }
}

function statusClasses(status: string): string {
  return status.toLowerCase() === "active"
    ? "bg-emerald-500 text-white"
    : "bg-amber-400 text-amber-950";
}

export function cardAccessibleLabel(card: Card): string {
  return `${formatStatus(card.brand)} card ending ${card.last4}, expires ${formatExpiry(
    card.expiry_month,
    card.expiry_year,
  )}, ${formatStatus(card.status)}`;
}

interface CardFaceProps {
  card: Card;
  className?: string;
}

/**
 * The seeded artwork already contains brand, masked number and expiry, so the
 * success state adds no duplicate text: the accessible name carries the data
 * and the only visible overlay is the status badge. If the art request fails,
 * the same data is rendered as text over a brand gradient.
 */
export function CardFace({ card, className = "" }: CardFaceProps) {
  const [url, setUrl] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let active = true;
    setUrl(null);
    setFailed(false);
    loadCardArt(card.art_url)
      .then((objectUrl) => {
        if (active) setUrl(objectUrl);
      })
      .catch(() => {
        if (active) setFailed(true);
      });
    return () => {
      active = false;
    };
  }, [card.art_url]);

  const label = cardAccessibleLabel(card);
  const expiry = formatExpiry(card.expiry_month, card.expiry_year);
  const status = formatStatus(card.status);

  return (
    <div
      role="img"
      aria-label={label}
      className={`relative aspect-[640/404] overflow-hidden rounded-2xl bg-gradient-to-br ${gradientFor(
        card.brand,
      )} shadow-lg ${className}`}
    >
      {url ? (
        <img
          src={url}
          alt=""
          onError={() => setFailed(true)}
          className="h-full w-full object-cover"
        />
      ) : failed ? (
        <div className="flex h-full w-full flex-col justify-between p-4 text-white sm:p-6">
          <span className="text-sm font-bold tracking-widest text-white/90">SME DEMO</span>
          <div className="space-y-2">
            <p className="text-2xl font-semibold tracking-wider">
              {formatMaskedNumber(card.last4)}
            </p>
            <p className="text-sm text-white/85">
              {formatStatus(card.brand)} · {expiry}
            </p>
          </div>
        </div>
      ) : (
        <div className="h-full w-full animate-pulse bg-slate-400/70" aria-hidden="true" />
      )}

      <span
        className={`absolute right-3 top-3 rounded-full px-2.5 py-1 text-xs font-semibold shadow ${statusClasses(
          card.status,
        )}`}
      >
        {status}
      </span>
    </div>
  );
}
