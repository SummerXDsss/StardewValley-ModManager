import { useState, useEffect } from "react";
import { App } from "antd";
import { getDashboard } from "../api";
import type { Dashboard, InstalledMod } from "../types";

export function useDashboard() {
  const { message } = App.useApp();
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
      message.error(errorMessage);
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
