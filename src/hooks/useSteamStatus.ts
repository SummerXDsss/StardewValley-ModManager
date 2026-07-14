import { useEffect, useRef, useState } from "react";
import { getSteamStatus } from "../api";

const POLL_INTERVAL_MS = 5_000;

export function useSteamStatus() {
  const [running, setRunning] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string>();
  const inFlight = useRef(false);

  useEffect(() => {
    let active = true;

    const refresh = async () => {
      if (inFlight.current) return;
      inFlight.current = true;
      try {
        const status = await getSteamStatus();
        if (!active) return;
        setRunning(status.running);
        setError(undefined);
      } catch (nextError) {
        if (active) {
          setRunning(false);
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

  return { running, loading, error };
}
