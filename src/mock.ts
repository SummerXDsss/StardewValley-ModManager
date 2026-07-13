import type { Dashboard } from "./types";

export const demoDashboard: Dashboard = {
  installation: {
    path: "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Stardew Valley",
    executable: "Stardew Valley.exe",
    store: "Steam",
    version: "1.6.15",
  },
  smapi: {
    installed: true,
    version: "4.1.10",
    executable: "StardewModdingAPI.exe",
  },
  mods: [
    {
      id: "Pathoschild.ContentPatcher",
      name: "Content Patcher",
      author: "Pathoschild",
      version: "2.6.3",
      path: "Mods\\ContentPatcher",
      enabled: true,
      health: "healthy",
      dependencies: [],
    },
    {
      id: "spacechase0.GenericModConfigMenu",
      name: "Generic Mod Config Menu",
      author: "spacechase0",
      version: "1.14.1",
      path: "Mods\\GenericModConfigMenu",
      enabled: true,
      health: "warning",
      updateAvailable: "1.15.0",
      dependencies: [],
    },
    {
      id: "Example.SeasonalGarden",
      name: "Seasonal Garden",
      author: "Juniper",
      version: "1.3.0",
      path: "Mods\\.SeasonalGarden",
      enabled: false,
      health: "healthy",
      dependencies: ["Pathoschild.ContentPatcher"],
    },
  ],
  warnings: ["1 个 Mod 有可用更新"],
};

