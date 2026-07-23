import { webDarkTheme, webLightTheme } from "@fluentui/react-components";
import type { Theme } from "@fluentui/react-components";
import type { ResolvedTheme } from "./hooks/useThemeMode";

export const UI_FONT_FAMILY = [
  '"Noto Sans SC"',
  '"Noto Sans CJK SC"',
  '"Source Han Sans SC"',
  '"PingFang SC"',
  '"Hiragino Sans GB"',
  '"Microsoft YaHei UI"',
  '"Segoe UI"',
  "system-ui",
  "sans-serif",
].join(", ");

export function createAppTheme(resolvedTheme: ResolvedTheme): Theme {
  const base = resolvedTheme === "dark" ? webDarkTheme : webLightTheme;
  return {
    ...base,
    fontFamilyBase: UI_FONT_FAMILY,
    fontFamilyMonospace: '"Cascadia Mono", "SFMono-Regular", Consolas, monospace',
  };
}
