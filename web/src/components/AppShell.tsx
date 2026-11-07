import { NavLink, Outlet } from "react-router-dom";

function navClass({ isActive }: { isActive: boolean }): string {
  return `inline-flex min-h-[44px] items-center rounded-lg px-3 text-sm font-semibold ${
    isActive ? "bg-slate-900 text-white" : "text-slate-700 hover:bg-slate-200"
  }`;
}

export function AppShell() {
  return (
    <div className="min-h-screen">
      <header className="border-b border-slate-200 bg-white">
        <div className="mx-auto flex max-w-[1100px] items-center justify-between gap-3 px-4 py-2">
          <NavLink
            to="/wallet"
            className="inline-flex min-h-[44px] items-center text-base font-bold tracking-tight text-slate-900"
          >
            SME Demo
          </NavLink>
          <nav aria-label="Primary" className="flex items-center gap-1">
            <NavLink to="/wallet" className={navClass}>
              Wallet
            </NavLink>
            <NavLink to="/profile" className={navClass}>
              Profile
            </NavLink>
          </nav>
        </div>
      </header>
      <main className="mx-auto max-w-[1100px] px-4 py-6">
        <Outlet />
      </main>
    </div>
  );
}
