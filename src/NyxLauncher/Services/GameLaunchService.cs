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
    /// <summary>以版本优先的高级配置构造进程，保留加载器参数并接入兼容组件与外置认证；只构造进程，不启动或修改显卡偏好。</summary>
    public static async Task<Process> BuildAsync(string folder, InstalledVersion version, IsolationMode isolation, GameAccount account,
        CancellationToken token = default, MSession? authenticatedSession = null, string? javaPath = null, AdvancedLaunchSettings? advanced = null)
    {
        var session = authenticatedSession ?? CreateSession(account);
        if (session.UUID != account.Uuid || account.IsMicrosoft && (session.UserType != "msa" || string.IsNullOrEmpty(session.AccessToken)))
            throw new InvalidOperationException("启动会话与所选账户不一致。");
        if (account.IsExternal && (session.UserType != "mojang" || string.IsNullOrWhiteSpace(session.AccessToken)))
            throw new InvalidOperationException("外置账户必须提供有效认证会话。");
        string gameDirectory = VersionIsolation.Resolve(folder, version, isolation);
        var path = new MinecraftPath(folder);
        var parameters = MinecraftLauncherParameters.CreateDefault(path);
        parameters.VersionLoader = new LocalJsonVersionLoader(path);
        var launcher = new MinecraftLauncher(parameters);
        Directory.CreateDirectory(gameDirectory);
        var memory = MemoryAllocation.Snapshot();
        int allocated = MemoryAllocation.Resolve(MemorySettingsStore.Read(version), memory, version, MemoryAllocation.CountMods(folder, version, isolation));
        advanced ??= AdvancedLaunchSettingsStore.Read(version);
        var profile = await launcher.GetVersionAsync(version.Name, token);
        int major = javaPath is null ? RequiredJavaMajor(profile) : JavaRuntime.Inspect(Path.GetDirectoryName(javaPath)!)?.Major
            ?? throw new IOException("无法识别所选 Java 版本。");
        var option = new MLaunchOption
        {
            MaximumRamMb = allocated,
            Session = session,
            JavaPath = javaPath,
            ClientId = account.ClientId,
            GameLauncherName = "NyxLauncher",
            ArgumentDictionary = new Dictionary<string, string> { ["game_directory"] = gameDirectory }
        };
        LaunchArguments.Apply(option, advanced, major);
        var process = await launcher.BuildProcessAsync(version.Name, option, token);
        process.StartInfo.WorkingDirectory = gameDirectory;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        try
        {
            bool affectedLwjgl = profile.EnumerateToParent().SelectMany(x => x.Libraries ?? []).Any(x => x.Name is "org.lwjgl:lwjgl:3.4.0" or "org.lwjgl:lwjgl:3.4.1");
            string mainClass = profile.EnumerateToParent().Select(x => x.MainClass).FirstOrDefault(x => !string.IsNullOrEmpty(x))
                ?? throw new IOException("游戏清单缺少主类。");
            int actualMajor = JavaRuntime.Inspect(Path.GetDirectoryName(process.StartInfo.FileName)!)?.Major ?? major;
            if (advanced.Collector == GarbageCollector.Z && actualMajor < 15) throw new IOException("ZGC 需要 Java 15 或更新版本。");
            LaunchCompatibility.Apply(process.StartInfo, advanced, actualMajor, mainClass, affectedLwjgl);
            if (account.IsExternal) await AuthlibInjector.ConfigureAsync(process, folder, account.AuthServer!, token);
        }
        catch { process.Dispose(); throw; }
        return process;
    }

    /// <summary>创建名称与标准离线 UUID 一致的会话；不使用库的随机/其他 UUID 默认值，也不声明正版认证。</summary>
    internal static MSession CreateSession(GameAccount account)
    {
        if (account.IsMicrosoft || account.IsExternal) throw new InvalidOperationException("在线账户必须先通过在线认证，不能使用离线会话。");
        GameAccount.ValidateName(account.Name);
        var session = MSession.CreateOfflineSession(account.Name);
        session.UUID = account.Uuid;
        return session;
    }

    /// <summary>按继承清单选择 Java 并补齐资源，返回固定的启动路径；提供管理快照时缺失版本明确报错，不隐式替换手动选择。</summary>
    public static async Task<string?> PrepareAsync(string folder, InstalledVersion version, DownloadSettings settings,
        Action<LaunchStage, string> report, CancellationToken token, JavaSelection? java = null)
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
        string? javaPath = null;
        if (java is not null)
        {
            report(LaunchStage.CheckingDependencies, "选择兼容的 Java");
            int required = RequiredJavaMajor(profile);
            var bundledRoots = new[] { path.Runtime, Path.Combine(AppContext.BaseDirectory, "Java") }.Where(Directory.Exists);
            var bundled = java.SelectedHome is null ? JavaDiscovery.Scan(bundledRoots, null, token).Runtimes : [];
            javaPath = JavaRuntimeStore.Resolve(java, required, bundled).Executable;
        }
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
            if (extractor is JavaFileExtractor && javaPath is not null) continue;
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
        return javaPath;
    }

    /// <summary>从最近一层 Java 声明读取主版本，包括加载器继承；老清单没有声明时按 Java 8 处理，未知声明报错。</summary>
    internal static int RequiredJavaMajor(IVersion profile)
    {
        var requirement = profile.EnumerateToParent().Select(x => x.JavaVersion).FirstOrDefault(x => x is not null);
        if (requirement is null) return 8;
        if (int.TryParse(requirement.MajorVersion, out int major) && major > 0) return major;
        return requirement.Component switch
        {
            "jre-legacy" => 8, "java-runtime-alpha" => 16, "java-runtime-beta" or "java-runtime-gamma" or "java-runtime-gamma-snapshot" => 17,
            "java-runtime-delta" => 21, "java-runtime-epsilon" => 25,
            _ => throw new IOException("游戏清单声明了未知 Java 版本，请检查版本清单。")
        };
    }

    /// <summary>同步上报已验证文件计数；UI 统一切回调度线程，不生成虚构百分比或阶段。</summary>
    private static IProgress<InstallerProgressChangedEventArgs> Progress(LaunchStage stage, string title, Action<LaunchStage, string> report) =>
        new InlineProgress<InstallerProgressChangedEventArgs>(p => report(stage, $"{title} · {p.ProgressedTasks}/{p.TotalTasks}"));
}
