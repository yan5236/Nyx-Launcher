using System.Text.Json;

namespace NyxLauncher.Services;

/// <summary>原子保存游戏账户、加密认证缓存和当前选择；兼容旧版仅名称的离线配置。</summary>
public sealed class AccountStore
{
    private sealed record Settings(GameAccount[] Accounts, string? Selected);
    private readonly string _path;
    public IReadOnlyList<GameAccount> Accounts { get; private set; } = [];
    public GameAccount? Selected { get; private set; }
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
            foreach (var account in saved.Accounts)
            {
                if (account is null) throw new JsonException("账户为空。");
                account.Validate();
            }
            if (saved.Accounts.Select(x => x.IsExternal ? x.Key : x.Key.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Count() != saved.Accounts.Length)
                throw new JsonException("账户身份重复。");
            var selected = saved.Accounts.SingleOrDefault(x => x.Key == saved.Selected || !x.IsMicrosoft && !x.IsExternal && x.Name == saved.Selected);
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
        GameAccount.ValidateName(name);
        if (Accounts.Any(x => !x.IsMicrosoft && !x.IsExternal && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("该离线账户已存在。");
        var account = new GameAccount(name);
        Save(Accounts.Append(account).ToArray(), account);
    }

    /// <summary>保存已通过认证的微软身份；按游戏 UUID 更新改名账户，刷新期间被删除或替换的旧记录不得复活。</summary>
    public void SaveMicrosoft(GameAccount account, GameAccount? previous = null, bool select = true)
    {
        account.Validate();
        if (!account.IsMicrosoft) throw new ArgumentException("需要微软账户。");
        if (previous is not null && (!Accounts.Contains(previous) || previous.Key != account.Key))
            throw new InvalidOperationException("账户已改变或移除，请重新选择后登录。");
        var existing = Accounts.SingleOrDefault(x => x.Key == account.Key);
        var updated = existing is null ? Accounts.Append(account).ToArray() : Accounts.Select(x => x == existing ? account : x).ToArray();
        Save(updated, select || Selected == existing ? account : Selected);
    }

    /// <summary>保存外置角色并保留原账户选择；刷新时拒绝复活已删除账户或替换服务器身份。</summary>
    public void SaveExternal(GameAccount account, GameAccount? previous = null, bool select = true)
    {
        account.Validate();
        if (!account.IsExternal) throw new ArgumentException("需要外置账户。");
        if (previous is not null && (!Accounts.Contains(previous) || previous.Key != account.Key))
            throw new InvalidOperationException("账户已改变或移除，请重新登录。");
        var existing = Accounts.SingleOrDefault(x => x.Key == account.Key);
        var updated = existing is null ? Accounts.Append(account).ToArray() : Accounts.Select(x => x == existing ? account : x).ToArray();
        Save(updated, select || Selected == existing ? account : Selected);
    }

    /// <summary>只允许切换已登记身份；配置成功写入后通知账户页、侧栏和首页，已运行的游戏保持其原身份。</summary>
    public void Select(GameAccount account)
    {
        var selected = Accounts.SingleOrDefault(x => x == account) ?? throw new ArgumentException("账户未登记。");
        if (Selected == selected) return;
        Save(Accounts.ToArray(), selected);
    }

    /// <summary>删除已登记账户；删除当前身份时选中剩余首项，全部删除后清空选择，保存失败保留原列表和选择。</summary>
    public void Remove(GameAccount account)
    {
        if (!Accounts.Contains(account)) throw new ArgumentException("账户未登记。");
        var remaining = Accounts.Where(x => x != account).ToArray();
        Save(remaining, Selected == account ? remaining.FirstOrDefault() : Selected);
    }

    /// <summary>先原子替换配置再发布选择变化；空列表允许无选中账户，写入异常向调用者传播并清理本次临时文件。</summary>
    private void Save(GameAccount[] accounts, GameAccount? selected)
    {
        if (LoadError is not null) throw new IOException(LoadError);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings(accounts, selected?.Key)));
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Accounts = accounts;
        Selected = selected;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
