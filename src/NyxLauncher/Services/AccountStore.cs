using System.Text.Json;

namespace NyxLauncher.Services;

/// <summary>保存用户主动添加的离线账户和当前选择；首次启动为空，不生成演示身份。</summary>
public sealed class AccountStore
{
    private sealed record Settings(OfflineAccount[] Accounts, string? Selected);
    private readonly string _path;
    public IReadOnlyList<OfflineAccount> Accounts { get; private set; } = [];
    public OfflineAccount? Selected { get; private set; }
    public string? LoadError { get; private set; }
    public event EventHandler? Changed;

    /// <summary>读取账户配置并验证名称、重复项及当前选择；损坏文件保留原状，后续写入会报错。</summary>
    public AccountStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NyxLauncher", "accounts.json");
        try
        {
            if (!File.Exists(_path)) return;
            var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_path)) ?? throw new JsonException("账户设置为空。");
            if (saved.Accounts is null) throw new JsonException("缺少账户列表。");
            foreach (var account in saved.Accounts) OfflineAccount.ValidateName(account?.Name!);
            if (saved.Accounts.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != saved.Accounts.Length)
                throw new JsonException("账户名称重复。");
            var selected = saved.Accounts.SingleOrDefault(x => x.Name == saved.Selected);
            if (saved.Accounts.Length > 0 && selected is null || saved.Accounts.Length == 0 && saved.Selected is not null)
                throw new JsonException("当前账户不在账户列表中。");
            Accounts = saved.Accounts;
            Selected = selected;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { LoadError = "无法读取账户设置，请修复后重启：" + _path + "\n" + ex.Message; }
    }

    /// <summary>校验新账户并持久化；成功后自动选中，重复名称或保存失败不改变内存中的账户。</summary>
    public void Add(string name)
    {
        OfflineAccount.ValidateName(name);
        if (Accounts.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("该离线账户已存在。");
        var account = new OfflineAccount(name);
        Save(Accounts.Append(account).ToArray(), account);
    }

    /// <summary>只允许切换已登记身份；配置成功写入后通知账户页、侧栏和首页，已运行的游戏保持其原身份。</summary>
    public void Select(OfflineAccount account)
    {
        var selected = Accounts.SingleOrDefault(x => x == account) ?? throw new ArgumentException("账户未登记。");
        if (Selected == selected) return;
        Save(Accounts.ToArray(), selected);
    }

    /// <summary>删除已登记账户；删除当前身份时选中剩余首项，全部删除后清空选择，保存失败保留原列表和选择。</summary>
    public void Remove(OfflineAccount account)
    {
        if (!Accounts.Contains(account)) throw new ArgumentException("账户未登记。");
        var remaining = Accounts.Where(x => x != account).ToArray();
        Save(remaining, Selected == account ? remaining.FirstOrDefault() : Selected);
    }

    /// <summary>先原子替换配置再发布选择变化；空列表允许无选中账户，写入异常向调用者传播并清理本次临时文件。</summary>
    private void Save(OfflineAccount[] accounts, OfflineAccount? selected)
    {
        if (LoadError is not null) throw new IOException(LoadError);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings(accounts, selected?.Name)));
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Accounts = accounts;
        Selected = selected;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
