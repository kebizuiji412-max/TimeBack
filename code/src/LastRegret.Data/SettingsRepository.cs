using LastRegret.Core.Abstractions;
using LastRegret.Core.Config;

namespace LastRegret.Data;

/// <summary>设置仓储（键值表 + JSON 主体）。</summary>
public sealed class SettingsRepository : ISettingsRepository
{
    private readonly LastRegretDatabase _database;

    public SettingsRepository(LastRegretDatabase database) => _database = database;

    public AppSettings Load() => _database.LoadSettings();

    public void Save(AppSettings settings) => _database.SaveSettings(settings);

    public string? GetRaw(string key) => LastRegretDatabase.GetMeta(_database.Events, key);

    public void SetRaw(string key, string value) => LastRegretDatabase.SetMeta(_database.Events, key, value);
}
