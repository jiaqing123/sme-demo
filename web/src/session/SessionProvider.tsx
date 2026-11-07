import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from "react";
import { useNavigate } from "react-router-dom";
import { resetUnauthorizedNotification, setUnauthorizedHandler } from "../api/client";
import { clearCardArtCache } from "../lib/art";
import { clearWalletScroll } from "../lib/scroll";
import {
  clearSession,
  loadSession,
  writeSession,
  type Session,
} from "./storage";

interface SessionContextValue {
  session: Session | null;
  /** True when the stored session expired or a request returned 401. */
  sessionExpired: boolean;
  signIn: (token: string, expiresInSeconds: number) => void;
  signOut: () => void;
}

const SessionContext = createContext<SessionContextValue | null>(null);

export function SessionProvider({ children }: { children: ReactNode }) {
  const navigate = useNavigate();
  const [initial] = useState(() => loadSession());
  const [session, setSession] = useState<Session | null>(initial.session);
  const [sessionExpired, setSessionExpired] = useState(initial.expired);

  const expire = useCallback(() => {
    clearSession();
    clearCardArtCache();
    clearWalletScroll();
    setSession(null);
    setSessionExpired(true);
  }, []);

  const signIn = useCallback((token: string, expiresInSeconds: number) => {
    resetUnauthorizedNotification();
    const next = writeSession(token, expiresInSeconds);
    setSession(next);
    setSessionExpired(false);
  }, []);

  const signOut = useCallback(() => {
    clearSession();
    clearCardArtCache();
    clearWalletScroll();
    setSession(null);
    setSessionExpired(false);
    navigate("/login", { replace: true });
  }, [navigate]);

  // A 401 on an authenticated request clears the tab's session and returns to
  // sign-in. Login and verify pass auth:false, so their 401 never reaches this.
  useEffect(() => {
    setUnauthorizedHandler(() => {
      expire();
      navigate("/login?reason=expired", { replace: true });
    });
    return () => setUnauthorizedHandler(null);
  }, [expire, navigate]);

  // Expire the session at the server-provided instant even if the user is idle.
  useEffect(() => {
    if (!session) return undefined;
    const delay = Math.max(0, session.expiresAt - Date.now());
    const timer = window.setTimeout(() => {
      expire();
      navigate("/login?reason=expired", { replace: true });
    }, delay);
    return () => window.clearTimeout(timer);
  }, [session, expire, navigate]);

  const value = useMemo<SessionContextValue>(
    () => ({ session, sessionExpired, signIn, signOut }),
    [session, sessionExpired, signIn, signOut],
  );

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession(): SessionContextValue {
  const value = useContext(SessionContext);
  if (!value) {
    throw new Error("useSession must be used inside SessionProvider");
  }
  return value;
}
