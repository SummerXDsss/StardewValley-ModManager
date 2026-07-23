import { useEffect, useLayoutEffect, useMemo, useState } from "react";

export type ThemePreference = "light" | "dark" | "system";
export type ResolvedTheme = "light" | "dark";

const STORAGE_KEY = "valleySteward.theme.v1";
const DARK_MEDIA_QUERY = "(prefers-color-scheme: dark)";

export interface InitialThemeState {
  preference: ThemePreference;
  systemTheme: ResolvedTheme;
}

function readStoredPreference(): ThemePreference {
  if (typeof window === "undefined") return "system";
  try {
    const stored = window.localStorage.getItem(STORAGE_KEY);
    return stored === "light" || stored === "dark" || stored === "system" ? stored : "system";
  } catch {
    return "system";
  }
}

function readSystemTheme(): ResolvedTheme {
  if (typeof window === "undefined" || typeof window.matchMedia !== "function") return "light";
  return window.matchMedia(DARK_MEDIA_QUERY).matches ? "dark" : "light";
}

export function getInitialThemeState(): InitialThemeState {
  return {
    preference: readStoredPreference(),
    systemTheme: readSystemTheme(),
  };
}

export function resolveTheme({ preference, systemTheme }: InitialThemeState): ResolvedTheme {
  return preference === "system" ? systemTheme : preference;
}

export function applyResolvedTheme(resolvedTheme: ResolvedTheme) {
  if (typeof document === "undefined") return;
  document.documentElement.dataset.theme = resolvedTheme;
  document.documentElement.style.colorScheme = resolvedTheme;
  document.querySelector('meta[name="theme-color"]')
    ?.setAttribute("content", resolvedTheme === "dark" ? "#111713" : "#f7f5ee");
}

export function useThemeMode(initialState: InitialThemeState) {
  const [preference, setPreference] = useState<ThemePreference>(initialState.preference);
  const [systemTheme, setSystemTheme] = useState<ResolvedTheme>(initialState.systemTheme);
  const resolvedTheme = useMemo(
    () => resolveTheme({ preference, systemTheme }),
    [preference, systemTheme],
  );

  useEffect(() => {
    if (typeof window === "undefined" || typeof window.matchMedia !== "function") return;
    const media = window.matchMedia(DARK_MEDIA_QUERY);
    const handleChange = (event: MediaQueryListEvent) => setSystemTheme(event.matches ? "dark" : "light");
    if (typeof media.addEventListener === "function") {
      media.addEventListener("change", handleChange);
      return () => media.removeEventListener("change", handleChange);
    }
    media.addListener(handleChange);
    return () => media.removeListener(handleChange);
  }, []);

  useLayoutEffect(() => {
    applyResolvedTheme(resolvedTheme);
  }, [resolvedTheme]);

  useEffect(() => {
    try {
      window.localStorage.setItem(STORAGE_KEY, preference);
    } catch {
      // The selected theme still applies for this session when storage is unavailable.
    }
  }, [preference]);

  const toggleTheme = () => setPreference(resolvedTheme === "dark" ? "light" : "dark");

  return { preference, resolvedTheme, toggleTheme };
}
