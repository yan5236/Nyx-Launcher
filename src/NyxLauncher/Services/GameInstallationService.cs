using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CmlLib.Core;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.Forge.Installers;
using CmlLib.Core.Installer.Forge.Versions;
using CmlLib.Core.Installers;

namespace NyxLauncher.Services;

/// <summary>安装真实客户端、资源、Java 与加载器；版本清单在全部步骤完成后才发布到版本管理。</summary>
public sealed class GameInstallationService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FolderLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;
    private readonly string _staging;
    private readonly DownloadSettings _settings;

    /// <summary>为一个任务建立稳定暂存路径；同目录安装串行保护共享处理器输出，单任务内部并行下载依赖。</summary>
    public GameInstallationService(string root, string taskId, DownloadSettings settings)
    {
        ValidateName(taskId);
        _root = Path.GetFullPath(root);
        _staging = Path.Combine(_root, ".nyx-install", taskId);
        _settings = settings;
    }

    /// <summary>验证用户名称及远程版本坐标，拒绝越界、保留设备名、尾空格和无效文件名，失败不创建任务。</summary>
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\') ||
            name.EndsWith('.') || name.EndsWith(' ') || Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\.)", RegexOptions.IgnoreCase))
            throw new ArgumentException("名称无效，请使用普通文件名，不含路径、尾随空格或 Windows 保留名称。");
    }

    /// <summary>拒绝根目录到目标目录之间的链接，防止共享目录或暂存区重定向至用户其他文件。</summary>
    public static void EnsureNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("安装路径不能包含符号链接或目录联接：" + current);
    }

    /// <summary>按已选择的确切版本完成安装；取消保持已验证资源供继续使用，失败不发布版本，已存在的目标绝不覆盖。</summary>
    public async Task InstallAsync(string name, string game, LoaderVersion? loader, DownloadTransport transport,
        Action<double, string> report, CancellationToken token)
    {
        ValidateName(name); ValidateName(game);
        if (loader is not null) ValidateName(loader.Id);
        var folderLock = FolderLocks.GetOrAdd(_root, _ => new(1));
        report(0, "等待游戏目录中的其他安装完成");
        await folderLock.WaitAsync(token);
        try
        {
            EnsureNoLinks(_root);
            EnsureNoLinks(_staging);
            foreach (string directory in new[] { "libraries", "assets", "runtime", "versions" })
                EnsureNoLinks(Path.Combine(_root, directory));
            string destination = Path.Combine(_root, "versions", name);
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("版本名称已存在，不能覆盖。");
            var path = new MinecraftPath(_root) { Versions = Path.Combine(_staging, "versions") };
            Directory.CreateDirectory(path.Versions);
            using var http = new HttpClient(new InstallMetadataHandler(_settings.VersionSource));
            var parameters = MinecraftLauncherParameters.CreateDefault(path, http);
            parameters.GameInstaller = new GameDownloadInstaller(transport, _settings.MaxConnections, _root);
            var launcher = new MinecraftLauncher(parameters);
            var vanillaProgress = new InlineProgress<InstallerProgressChangedEventArgs>(p =>
                report(5 + 60d * p.ProgressedTasks / Math.Max(1, p.TotalTasks), "安装游戏资源 · " + p.Name));
            report(1, "获取游戏安装清单");
            await launcher.InstallAsync(game, vanillaProgress, null, token);
            string installed = game;
            if (loader is not null)
            {
                report(66, "安装 " + loader.Loader);
                installed = await InstallLoaderAsync(launcher, http, game, loader, report, token);
                // 在补依赖前合并父版本，避免加载器未声明 Java 时被安装库误判为旧版 Java 8。
                var profile = ReadMerged(path, installed, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                profile["id"] = installed;
                profile["jar"] = File.Exists(path.GetVersionJarPath(installed)) ? installed : game;
                profile.Remove("inheritsFrom");
                await File.WriteAllTextAsync(path.GetVersionJsonPath(installed), profile.ToJsonString(), token);
                await launcher.GetAllVersionsAsync(token);
                await launcher.InstallAsync(installed, new InlineProgress<InstallerProgressChangedEventArgs>(p =>
                    report(85 + 12d * p.ProgressedTasks / Math.Max(1, p.TotalTasks), "补全加载器依赖 · " + p.Name)), null, token);
            }
            token.ThrowIfCancellationRequested();
            report(98, "校验并发布版本");
            var merged = ReadMerged(path, installed, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            string jar = path.GetVersionJarPath(installed);
            if (!File.Exists(jar)) jar = path.GetVersionJarPath(game);
            if (!File.Exists(jar)) throw new IOException("安装缺少游戏客户端 JAR。");
            merged["id"] = name;
            merged["jar"] = name;
            merged.Remove("inheritsFrom");
            merged["patches"] = new JsonArray(new JsonObject { ["id"] = "game", ["version"] = game });
            string publish = Path.Combine(_staging, "publish");
            Directory.CreateDirectory(publish);
            File.Copy(jar, Path.Combine(publish, name + ".jar"), true);
            await File.WriteAllTextAsync(Path.Combine(publish, name + ".json"), merged.ToJsonString(), token);
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(_root, "versions"));
            Directory.Move(publish, destination);
        }
        finally { folderLock.Release(); }
    }

    /// <summary>Fabric 使用官方 profile；Forge/NeoForge 执行安装包真实处理器，采用用户所选完整坐标，不自动换成最新版。</summary>
    private async Task<string> InstallLoaderAsync(MinecraftLauncher launcher, HttpClient http, string game,
        LoaderVersion loader, Action<double, string> report, CancellationToken token)
    {
        var path = launcher.MinecraftPath;
        if (loader.Loader == LoaderKind.Fabric)
        {
            string json = await http.GetStringAsync($"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(game)}/{Uri.EscapeDataString(loader.Version)}/profile/json", token);
            var profile = JsonNode.Parse(json)!.AsObject();
            string id = profile["id"]!.GetValue<string>();
            ValidateName(id);
            if (profile["inheritsFrom"]?.GetValue<string>() != game) throw new IOException("Fabric 清单返回了其他游戏版本。");
            Directory.CreateDirectory(Path.Combine(path.Versions, id));
            await File.WriteAllTextAsync(path.GetVersionJsonPath(id), json, token);
            return id;
        }
        var baseVersion = await launcher.GetVersionAsync(game, token);
        string? java = launcher.GetJavaPath(baseVersion) ?? launcher.GetDefaultJavaPath();
        if (java is null || !File.Exists(java)) throw new IOException("没有找到安装处理器所需的 Java。");
        string artifact = loader.Loader == LoaderKind.Forge || game == "1.20.1" ? "forge" : "neoforge";
        string group = loader.Loader == LoaderKind.Forge ? "minecraftforge" : "neoforged";
        string coordinate = loader.Loader == LoaderKind.NeoForge && game == "1.20.1" && !loader.Id.StartsWith(game + "-") ? game + "-" + loader.Id : loader.Id;
        string url = loader.Loader == LoaderKind.Forge ?
            $"https://maven.minecraftforge.net/net/{group}/{artifact}/{coordinate}/{artifact}-{coordinate}-installer.jar" :
            $"https://maven.neoforged.net/releases/net/{group}/{artifact}/{coordinate}/{artifact}-{coordinate}-installer.jar";
        IForgeInstaller installer;
        if (loader.Loader == LoaderKind.Forge)
        {
            var forge = new ForgeVersion(game, loader.Version) { Files = [new ForgeVersionFile { Type = "installer", DirectUrl = url }] };
            installer = new ForgeInstallerVersionMapper().CreateInstaller(forge);
        }
        else installer = new ForgeV12Installer(new(game, loader.Version, "neoforge-" + loader.Version, url, null));
        var options = new ForgeInstallOptions
        {
            JavaPath = java, CancellationToken = token, SkipIfAlreadyInstalled = false,
            FileProgress = new InlineProgress<InstallerProgressChangedEventArgs>(p => report(67 +
                17d * p.ProgressedTasks / Math.Max(1, p.TotalTasks), "安装 " + loader.Loader + " · " + p.Name)),
            InstallerOutput = new InlineProgress<string>(_ => report(80, "执行 " + loader.Loader + " 安装处理器"))
        };
        await installer.Install(path, launcher.GameInstaller, options);
        return installer.VersionName;
    }

    /// <summary>将继承清单合并为独立版本；子层标量覆盖、参数拼接、同坐标库覆盖，保留游戏依赖与加载器参数。</summary>
    internal static JsonObject ReadMerged(MinecraftPath path, string id, HashSet<string> seen)
    {
        ValidateName(id);
        if (!seen.Add(id) || seen.Count > 16) throw new IOException("安装清单继承循环或层级过深。");
        var child = JsonNode.Parse(File.ReadAllText(path.GetVersionJsonPath(id)))!.AsObject();
        if (child["inheritsFrom"] is not JsonValue parentId) return child;
        var parent = ReadMerged(path, parentId.GetValue<string>(), seen);
        foreach (var property in child)
        {
            if (property.Key == "libraries" && property.Value is JsonArray libraries)
            {
                var merged = (parent["libraries"]?.AsArray() ?? []).Concat(libraries)
                    .Where(x => x is not null).GroupBy(x =>
                    {
                        string[] coordinate = x!["name"]!.GetValue<string>().Split(':');
                        return string.Join(':', coordinate.Take(2).Concat(coordinate.Skip(3)));
                    })
                    .Select(x => x.Last()!.DeepClone()).ToArray();
                parent["libraries"] = new JsonArray(merged);
            }
            else if (property.Key == "arguments" && property.Value is JsonObject arguments)
            {
                var combined = parent["arguments"] as JsonObject ?? new JsonObject();
                foreach (var argument in arguments)
                    combined[argument.Key] = new JsonArray((combined[argument.Key]?.AsArray() ?? [])
                        .Concat(argument.Value!.AsArray()).Select(x => x?.DeepClone()).ToArray());
                if (parent["arguments"] is null) parent["arguments"] = combined;
            }
            else parent[property.Key] = property.Value?.DeepClone();
        }
        return parent;
    }

    /// <summary>只删除本任务 ID 的暂存目录；先验证绝对范围与链接，已发布版本和共享资源不在删除范围内。</summary>
    public void Cleanup()
    {
        string allowed = Path.Combine(_root, ".nyx-install") + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(_staging).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new IOException("暂存路径越界。");
        if (!Directory.Exists(_staging)) return;
        EnsureNoLinks(_staging);
        foreach (string entry in Directory.EnumerateFileSystemEntries(_staging, "*", SearchOption.AllDirectories)) EnsureNoLinks(entry);
        Directory.Delete(_staging, true);
    }
}

/// <summary>同步报告后台安装进度，避免 Progress 在无 UI 上下文时乱序投递旧阶段。</summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    /// <summary>在调用线程传递进度；UI 切换由任务管理器集中完成。</summary>
    public void Report(T value) => report(value);
}
