using System.Diagnostics;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.VersionLoader;
using CmlLib.Core.FileExtractors;
using CmlLib.Core.Version;
using CmlLib.Core.Installers;

namespace NyxLauncher.Services;

/// <summary>从真实本地清单生成启动进程，将隔离策略用于 gameDir 与进程工作目录。</summary>
public static class GameLaunchService
{
    /// <summary>为指定离线身份构造进程；按隔离策略设置存档目录，仅构造参数，不启动游戏或下载资源。</summary>
    public static async Task<Process> BuildAsync(string folder, InstalledVersion version, IsolationMode isolation, OfflineAccount account, CancellationToken token = default)
    {
        string gameDirectory = VersionIsolation.Resolve(folder, version, isolation);
        var path = new MinecraftPath(folder);
        var parameters = MinecraftLauncherParameters.CreateDefault(path);
        parameters.VersionLoader = new LocalJsonVersionLoader(path);
        var launcher = new MinecraftLauncher(parameters);
        Directory.CreateDirectory(gameDirectory);
        var process = await launcher.BuildProcessAsync(version.Name, new MLaunchOption
        {
            Session = CreateSession(account),
            GameLauncherName = "NyxLauncher",
            ArgumentDictionary = new Dictionary<string, string> { ["game_directory"] = gameDirectory }
        }, token);
        process.StartInfo.WorkingDirectory = gameDirectory;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        return process;
    }

    /// <summary>创建名称与标准离线 UUID 一致的会话；不使用库的随机/其他 UUID 默认值，也不声明正版认证。</summary>
    internal static MSession CreateSession(OfflineAccount account)
    {
        OfflineAccount.ValidateName(account.Name);
        var session = MSession.CreateOfflineSession(account.Name);
        session.UUID = account.Uuid;
        return session;
    }

    /// <summary>先校验并补齐资源，再检查客户端、库与 Java；所有文件复用统一下载器的哈希验证、源策略和取消机制。</summary>
    public static async Task PrepareAsync(string folder, InstalledVersion version, DownloadSettings settings,
        Action<LaunchStage, string> report, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var current = InstalledVersionService.Scan(folder, token).SingleOrDefault(x => x.Name == version.Name);
        if (current is null || current.Error is not null) throw new IOException(current?.Error ?? "所选版本已不存在。");
        GameInstallationService.EnsureNoLinks(current.DirectoryPath);
        var path = new MinecraftPath(folder);
        using var http = new HttpClient(new LaunchMetadataHandler(folder, settings.VersionSource, token));
        using var transport = new DownloadTransport(settings);
        var parameters = MinecraftLauncherParameters.CreateDefault(path, http);
        parameters.VersionLoader = new LocalJsonVersionLoader(path);
        var launcher = new MinecraftLauncher(parameters);
        var profile = await launcher.GetVersionAsync(version.Name, token);
        var installer = new GameDownloadInstaller(transport, folder);
        report(LaunchStage.CheckingAssets, "检查资源完整性");
        foreach (var inherited in profile.EnumerateToParent())
        {
            if (inherited.AssetIndex is { } index)
            {
                GameInstallationService.ValidateName(index.Id!);
                string indexPath = path.GetIndexFilePath(index.Id!);
                GameInstallationService.EnsureNoLinks(indexPath);
                if (!string.IsNullOrEmpty(index.Url))
                    await transport.DownloadAsync(DownloadTransport.ValidateUrl(index.Url), indexPath, index.Sha1, index.Size, true, false, null, token);
            }
            var files = await new AssetFileExtractor(http).Extract(path, inherited, launcher.RulesContext, token);
            await installer.Install(files, Progress(LaunchStage.CheckingAssets, "检查资源完整性", report), null, token);
        }
        report(LaunchStage.CheckingDependencies, "检查依赖完整性");
        foreach (var extractor in launcher.FileExtractors.Where(x => x is not AssetFileExtractor))
        {
            // Java 使用有效继承属性所属清单，避免加载器子清单未声明 Java 时误装 Java 8。
            var profiles = extractor is JavaFileExtractor ?
                new[] { profile.EnumerateToParent().FirstOrDefault(x => x.JavaVersion is not null) ?? profile } : profile.EnumerateToParent();
            foreach (var inherited in profiles)
            {
                token.ThrowIfCancellationRequested();
                GameInstallationService.ValidateName(inherited.MainJarId);
                var files = await extractor.Extract(path, inherited, launcher.RulesContext, token);
                await installer.Install(files, Progress(LaunchStage.CheckingDependencies, "检查依赖完整性", report), null, token);
            }
        }
    }

    /// <summary>同步上报已验证文件计数；UI 统一切回调度线程，不生成虚构百分比或阶段。</summary>
    private static IProgress<InstallerProgressChangedEventArgs> Progress(LaunchStage stage, string title, Action<LaunchStage, string> report) =>
        new InlineProgress<InstallerProgressChangedEventArgs>(p => report(stage, $"{title} · {p.ProgressedTasks}/{p.TotalTasks}"));
}
