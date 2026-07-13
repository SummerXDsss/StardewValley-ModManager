import { useCallback, useEffect, useRef, useState } from "react";
import { getGameProcessStatus } from "../api";
import type { GameProcessStatus } from "../types";

const stoppedStatus: GameProcessStatus = { state: "stopped", running: false };
const pollIntervalMs = 1_500;

export function useGameProcess() {
  const [status, setStatus] = useState<GameProcessStatus>(stoppedStatus);
  const [monitoring, setMonitoring] = useState(true);
  const [monitorError, setMonitorError] = useState<string>();
  const requestSequence = useRef(0);

  const acceptStatus = useCallback((nextStatus: GameProcessStatus) => {
    requestSequence.current += 1;
    setStatus(nextStatus);
    setMonitoring(false);
    setMonitorError(undefined);
  }, []);

  const refresh = useCallback(async () => {
    const sequence = ++requestSequence.current;
    try {
      const nextStatus = await getGameProcessStatus();
      if (sequence === requestSequence.current) {
        setStatus(nextStatus);
        setMonitorError(undefined);
      }
      return nextStatus;
    } catch (error) {
      if (sequence === requestSequence.current) setMonitorError(String(error));
      throw error;
    } finally {
      if (sequence === requestSequence.current) setMonitoring(false);
    }
  }, []);

  useEffect(() => {
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;

    const poll = async () => {
      try {
        await refresh();
      } catch {
        // The latest request records its own error state.
      } finally {
        if (!cancelled) {
          timer = setTimeout(poll, pollIntervalMs);
        }
      }
    };

    void poll();
    return () => {
      cancelled = true;
      requestSequence.current += 1;
      if (timer) clearTimeout(timer);
    };
  }, [refresh]);

  return { status, monitoring, monitorError, refresh, acceptStatus };
}
