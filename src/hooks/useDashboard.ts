import { useState, useEffect } from "react";
import { App } from "antd";
import { getDashboard } from "../api";
import type { Dashboard, InstalledMod } from "../types";

export function useDashboard() {
  const { message } = App.useApp();
  const [dashboard, setDashboard] = useState<Dashboard>();
  const [loading, setLoading] = useState(true);

  const refresh = async () => {
    setLoading(true);
    try {
      const data = await getDashboard();
      setDashboard(data);
      return data;
    } catch (error) {
      message.error(String(error));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void refresh();
  }, []);

  const updateMod = (id: string, patch: Partial<InstalledMod>) =>
    setDashboard((current) =>
      current ? { ...current, mods: current.mods.map((mod) => (mod.id === id ? { ...mod, ...patch } : mod)) } : current,
    );

  return {
    dashboard,
    setDashboard,
    loading,
    setLoading,
    refresh,
    updateMod,
  };
}
