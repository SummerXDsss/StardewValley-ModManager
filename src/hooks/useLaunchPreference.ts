import { useState } from "react";

export type LaunchPreference = "smapi" | "vanilla" | "profile";

const launchPreferences: LaunchPreference[] = ["smapi", "vanilla", "profile"];

export function useLaunchPreference() {
  const [rememberLaunch, setRememberLaunch] = useState(() => localStorage.getItem("rememberLaunch") === "true");
  const [launchPreference, setLaunchPreference] = useState<LaunchPreference>(() => {
    const saved = localStorage.getItem("launchPreference") as LaunchPreference | null;
    return saved && launchPreferences.includes(saved) ? saved : "smapi";
  });

  const changeRememberLaunch = (checked: boolean) => {
    setRememberLaunch(checked);
    localStorage.setItem("rememberLaunch", String(checked));
    if (!checked) {
      setLaunchPreference("smapi");
      localStorage.removeItem("launchPreference");
    }
  };

  const updateLaunchPreference = (choice: LaunchPreference) => {
    if (rememberLaunch) {
      setLaunchPreference(choice);
      localStorage.setItem("launchPreference", choice);
    }
  };

  return {
    rememberLaunch,
    launchPreference,
    changeRememberLaunch,
    updateLaunchPreference,
  };
}
