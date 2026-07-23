import { useState, useEffect } from "react";
import { getDashboard } from "../api";
import { useAppUi } from "../components/shared";
import type { Dashboard, InstalledMod } from "../types";

export function useDashboard() {
  const { notify } = useAppUi();
  const [dashboard, setDashboard] = useState<Dashboard>();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string>();

  const refresh = async () => {
    setLoading(true);
    setError(undefined);
    try {
      const data = await getDashboard();
      setDashboard(data);
      return data;
    } catch (nextError) {
      const errorMessage = String(nextError);
      setError(errorMessage);
      notify("error", "无法读取本地游戏状态", errorMessage);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void refresh();
  }, []);

  const updateMod = (path: string, patch: Partial<InstalledMod>) =>
    setDashboard((current) =>
      current
        ? { ...current, mods: current.mods.map((mod) => (mod.path === path ? { ...mod, ...patch } : mod)) }
        : current,
    );

  return {
    dashboard,
    setDashboard,
    loading,
    error,
    setLoading,
    refresh,
    updateMod,
  };
}
