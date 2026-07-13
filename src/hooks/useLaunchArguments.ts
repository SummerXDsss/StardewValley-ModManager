import { useState } from "react";
import type { LaunchArgumentSettings, LaunchTarget } from "../types";

const storageKey = "launchArguments";
const emptySettings: LaunchArgumentSettings = { smapi: [], vanilla: [] };

function normalizeArguments(value: unknown): string[] {
  if (!Array.isArray(value)) return [];
  return value
    .filter((argument): argument is string => typeof argument === "string")
    .filter((argument) => argument.trim().length > 0 && argument.length <= 512)
    .slice(0, 32);
}

function loadSettings(): LaunchArgumentSettings {
  try {
    const saved = JSON.parse(localStorage.getItem(storageKey) ?? "null") as Partial<LaunchArgumentSettings> | null;
    if (!saved) return emptySettings;
    return {
      smapi: normalizeArguments(saved.smapi),
      vanilla: normalizeArguments(saved.vanilla),
    };
  } catch {
    return emptySettings;
  }
}

export function useLaunchArguments() {
  const [launchArguments, setLaunchArguments] = useState<LaunchArgumentSettings>(loadSettings);

  const updateLaunchArguments = (target: LaunchTarget, argumentsList: string[]) => {
    setLaunchArguments((current) => {
      const next = { ...current, [target]: normalizeArguments(argumentsList) };
      try {
        localStorage.setItem(storageKey, JSON.stringify(next));
      } catch {
        // Keep the current session usable if WebView storage is unavailable.
      }
      return next;
    });
  };

  return { launchArguments, updateLaunchArguments };
}
