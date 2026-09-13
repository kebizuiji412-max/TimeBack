using System.Text.Json;
using LastRegret.Core.Config;
using LastRegret.Windows.Sqlite;

namespace LastRegret.Data;

/// <summary>应用设置的持久化：以 JSON 主体存进当前数据库的 meta 表，并能从 meta 表读回。</summary>
public static class DatabaseSettings
{
    public const string SettingsKey = "app_settings";

    public static AppSettings Load(SqliteConnection conn)
    {
        var raw = LastRegretDatabase.GetMeta(conn, SettingsKey);
        if (raw is null) return new AppSettings();
        try
        {
            var parsed = JsonSerializer.Deserialize<AppSettings>(raw, AppSettings.JsonOpts);
            return parsed ?? new AppSettings();
        }
        catch (JsonException)
        {
            // 设置损坏不能导致程序不可用
            LastRegretDatabase.SetMeta(conn, SettingsKey + ".corrupt." + DateTime.UtcNow.Ticks, raw);
            return new AppSettings();
        }
    }

    public static void Save(SqliteConnection conn, AppSettings settings) =>
        LastRegretDatabase.SetMeta(conn, SettingsKey, JsonSerializer.Serialize(settings, AppSettings.JsonOpts));
}
