import React from "react";
import ReactDOM from "react-dom/client";
import { FluentProvider } from "@fluentui/react-components";
import App from "./App";
import { AppUiProvider } from "./components/shared";
import { applyResolvedTheme, getInitialThemeState, resolveTheme, useThemeMode } from "./hooks";
import { createAppTheme } from "./theme";
import "./styles.css";

const initialThemeState = getInitialThemeState();
applyResolvedTheme(resolveTheme(initialThemeState));

function Root() {
  const { resolvedTheme, toggleTheme } = useThemeMode(initialThemeState);

  return (
    <FluentProvider theme={createAppTheme(resolvedTheme)} className="fluent-root">
      <AppUiProvider>
        <App resolvedTheme={resolvedTheme} onToggleTheme={toggleTheme} />
      </AppUiProvider>
    </FluentProvider>
  );
}

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <Root />
  </React.StrictMode>,
);
