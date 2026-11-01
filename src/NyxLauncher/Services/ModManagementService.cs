using System.Security.Cryptography;

namespace NyxLauncher.Services;

/// <summary>管理实际游戏目录中的模组文件，所有写入复核归属、链接和旧文件内容。</summary>
public static class ModManagementService
{
    /// <summary>根据当前版本和隔离策略解析 mods，不创建目录；无效版本或链接抛出 I/O 异常。</summary>
    public static string Resolve(string root, InstalledVersion version, IsolationMode isolation)
    {
        string directory = Path.Combine(ResourceInstallationService.ResolveRoot(root, version, version.GameVersion, isolation), "mods");
        GameInstallationService.EnsureNoLinks(directory);
        return directory;
    }

    /// <summary>只读取直属 JAR 和 JAR.disabled，逐个计算 SHA-1；取消或不可读文件向调用方报告，不扫描子目录。</summary>
    public static async Task<IReadOnlyList<InstalledMod>> ScanAsync(string directory, CancellationToken token)
    {
        GameInstallationService.EnsureNoLinks(directory);
        if (!Directory.Exists(directory)) return [];
        var result = new List<InstalledMod>();
        foreach (string path in Directory.EnumerateFiles(directory).Where(IsMod).Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            GameInstallationService.EnsureNoLinks(path);
            using var input = File.OpenRead(path);
            result.Add(new(path, Convert.ToHexStringLower(await SHA1.HashDataAsync(input, token))));
        }
        return result;
    }

    /// <summary>仅允许已启用和按本应用约定禁用的模组后缀，不将临时、备份或其他文件作为模组。</summary>
    private static bool IsMod(string path) => path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);

    /// <summary>确认文件仍是当前目录的直属模组并且哈希未变化；防止陈旧列表操作外部文件或替换后的文件。</summary>
    public static void Validate(string directory, InstalledMod mod)
    {
        if (!IsMod(mod.Path) || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(mod.Path)), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
            throw new IOException("模组文件不属于当前版本。");
        GameInstallationService.EnsureNoLinks(mod.Path);
        using var input = File.OpenRead(mod.Path);
        if (!Convert.ToHexStringLower(SHA1.HashData(input)).Equals(mod.Sha1, StringComparison.OrdinalIgnoreCase))
            throw new IOException("模组文件已变化，请重新打开模组管理。");
    }

    /// <summary>通过增删 .disabled 后缀切换状态；目标存在时拒绝覆盖，成功返回新的文件项。</summary>
    public static InstalledMod Toggle(string directory, InstalledMod mod)
    {
        Validate(directory, mod);
        string target = mod.Disabled ? mod.Path[..^9] : mod.Path + ".disabled";
        GameInstallationService.EnsureNoLinks(target);
        File.Move(mod.Path, target);
        return mod with { Path = target };
    }

    /// <summary>从较新的正式发布中选择精确匹配游戏版本与加载器的首个运行文件；同发布及相同哈希不视为更新。</summary>
    public static ModrinthModFile? SelectUpdate(InstalledMod mod, InstalledVersion version, IEnumerable<ModrinthModFile> files) =>
        files.Where(x => x.ProjectId == mod.Project?.Id && x.Channel == "release" && x.Published > mod.Published &&
            x.VersionId != mod.VersionId && !x.Sha1.Equals(mod.Sha1, StringComparison.OrdinalIgnoreCase) &&
            x.GameVersions.Contains(version.GameVersion) &&
            x.Loaders.Contains(version.Loader.Split(':')[0].Trim(), StringComparer.OrdinalIgnoreCase))
        .OrderByDescending(x => x.Published).FirstOrDefault();

    /// <summary>下载新版至独立暂存文件，校验成功且复核运行状态后替换旧版；保留禁用状态，同名冲突和取消不改旧文件，发布失败尝试回滚。</summary>
    public static async Task UpdateAsync(string directory, InstalledMod mod, ModrinthModFile update,
        DownloadTransport transport, Action ensureWritable, CancellationToken token)
    {
        update.Validate();
        if (update.ProjectId != mod.Project?.Id) throw new IOException("更新与当前模组不匹配。");
        Validate(directory, mod);
        string target = Path.Combine(directory, update.FileName + (mod.Disabled ? ".disabled" : ""));
        bool samePath = target.Equals(mod.Path, StringComparison.OrdinalIgnoreCase);
        string opposite = mod.Disabled ? target[..^9] : target + ".disabled";
        GameInstallationService.EnsureNoLinks(target);
        GameInstallationService.EnsureNoLinks(opposite);
        if ((!samePath && Path.Exists(target)) || Path.Exists(opposite)) throw new IOException("新版文件已存在，未覆盖。");
        string staging = Path.Combine(directory, ".nyx-update-" + Guid.NewGuid().ToString("N") + ".tmp");
        string backup = staging + ".bak";
        try
        {
            await transport.DownloadAsync(update.Url, staging, update.Sha1, update.Size, false, true, null, token, mirrorResource: true);
            token.ThrowIfCancellationRequested();
            ensureWritable();
            Validate(directory, mod);
            GameInstallationService.EnsureNoLinks(target);
            if (Path.Exists(opposite)) throw new IOException("新版文件已存在，未覆盖。");
            if (samePath) File.Replace(staging, target, null);
            else
            {
                File.Move(mod.Path, backup);
                try { File.Move(staging, target); }
                catch
                {
                    try { File.Move(backup, mod.Path); }
                    catch (Exception ex) { throw new IOException("更新失败，旧文件保留在：" + backup, ex); }
                    throw;
                }
                // 新版已发布；旧备份清理失败时保留 .bak，避免将成功替换误报为失败。
                try { File.Delete(backup); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
}
