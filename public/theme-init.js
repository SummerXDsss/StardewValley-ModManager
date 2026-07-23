(function () {
  var preference = "system";
  try {
    var stored = window.localStorage.getItem("valleySteward.theme.v1");
    if (stored === "light" || stored === "dark" || stored === "system") preference = stored;
  } catch (_) {
    preference = "system";
  }

  var systemTheme = window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches
    ? "dark"
    : "light";
  var resolvedTheme = preference === "system" ? systemTheme : preference;
  document.documentElement.dataset.theme = resolvedTheme;
  document.documentElement.style.colorScheme = resolvedTheme;
  document.querySelector('meta[name="theme-color"]')
    .setAttribute("content", resolvedTheme === "dark" ? "#111713" : "#f7f5ee");
}());
