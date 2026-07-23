import { useEffect, useRef, useState } from "react";
import { getSteamStatus } from "../api";
import type { SteamStatus } from "../types";

const POLL_INTERVAL_MS = 5_000;

export function useSteamStatus() {
  const [status, setStatus] = useState<SteamStatus>({ running: false });
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string>();
  const inFlight = useRef(false);

  useEffect(() => {
    let active = true;

    const refresh = async () => {
      if (inFlight.current) return;
      inFlight.current = true;
      try {
        const nextStatus = await getSteamStatus();
        if (!active) return;
        setStatus(nextStatus);
        setError(undefined);
      } catch (nextError) {
        if (active) {
          setStatus({ running: false });
          setError(String(nextError));
        }
      } finally {
        inFlight.current = false;
        if (active) setLoading(false);
      }
    };

    void refresh();
    const interval = window.setInterval(() => void refresh(), POLL_INTERVAL_MS);
    return () => {
      active = false;
      window.clearInterval(interval);
    };
  }, []);

  return {
    status,
    running: status.running,
    identity: status.identity,
    loading,
    error,
  };
}
