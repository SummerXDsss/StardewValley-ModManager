namespace ValleySteward.WinUI.Services;

public static class AppPaths
{
    public static string ConfigDirectory { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "com.summerxdsss.valleysteward");

    public static string GamePathConfig => System.IO.Path.Combine(ConfigDirectory, "game-path.json");
    public static string UiSettingsConfig => System.IO.Path.Combine(ConfigDirectory, "ui-settings-v1.json");
    public static string ActivityHistoryConfig => System.IO.Path.Combine(ConfigDirectory, "activity-history-v1.json");
}
