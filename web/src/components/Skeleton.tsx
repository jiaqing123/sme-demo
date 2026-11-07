export function CardGridSkeleton({ count = 3 }: { count?: number }) {
  return (
    <div
      role="status"
      aria-label="Loading cards"
      className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3"
    >
      {Array.from({ length: count }, (_, index) => (
        <div
          key={index}
          className="aspect-[640/404] animate-pulse rounded-2xl bg-slate-300"
        />
      ))}
    </div>
  );
}

export function ProfileSkeleton() {
  return (
    <div role="status" aria-label="Loading profile" className="space-y-6">
      <div className="h-7 w-40 animate-pulse rounded bg-slate-300" />
      <dl className="divide-y divide-slate-200 rounded-xl border border-slate-200 bg-white">
        {Array.from({ length: 4 }, (_, index) => (
          <div key={index} className="flex gap-4 p-4">
            <div className="h-4 w-24 animate-pulse rounded bg-slate-300" />
            <div className="h-4 w-40 animate-pulse rounded bg-slate-200" />
          </div>
        ))}
      </dl>
    </div>
  );
}

export function TransactionListSkeleton({ rows = 5 }: { rows?: number }) {
  return (
    <div role="status" aria-label="Loading transactions" className="space-y-3">
      <div className="h-4 w-28 animate-pulse rounded bg-slate-300" />
      <div className="divide-y divide-slate-200 overflow-hidden rounded-xl border border-slate-200 bg-white">
        {Array.from({ length: rows }, (_, index) => (
          <div key={index} className="flex items-center justify-between gap-4 p-4">
            <div className="space-y-2">
              <div className="h-4 w-36 animate-pulse rounded bg-slate-300" />
              <div className="h-3 w-16 animate-pulse rounded bg-slate-200" />
            </div>
            <div className="h-4 w-20 animate-pulse rounded bg-slate-300" />
          </div>
        ))}
      </div>
    </div>
  );
}

export function CardSummarySkeleton() {
  return (
    <div role="status" aria-label="Loading card" className="space-y-6">
      <div className="h-5 w-28 animate-pulse rounded bg-slate-300" />
      <div className="aspect-[640/404] w-full max-w-md animate-pulse rounded-2xl bg-slate-300" />
      <div className="space-y-3">
        <div className="h-4 w-48 animate-pulse rounded bg-slate-200" />
        <div className="h-4 w-36 animate-pulse rounded bg-slate-200" />
      </div>
    </div>
  );
}
