using System.Diagnostics;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.VersionLoader;

namespace NyxLauncher.Services;

/// <summary>从真实本地清单生成启动进程，将隔离策略用于 gameDir 与进程工作目录。</summary>
public static class GameLaunchService
{
    /// <summary>仅读取已安装文件；按当前隔离设置决定存档/模组目录，共享依赖路径不变，不隐式发起下载或伪装微软登录。</summary>
    public static async Task<Process> BuildAsync(string folder, InstalledVersion version, IsolationMode isolation, CancellationToken token = default)
    {
        string gameDirectory = VersionIsolation.Resolve(folder, version, isolation);
        var path = new MinecraftPath(folder);
        var parameters = MinecraftLauncherParameters.CreateDefault(path);
        parameters.VersionLoader = new LocalJsonVersionLoader(path);
        var launcher = new MinecraftLauncher(parameters);
        Directory.CreateDirectory(gameDirectory);
        var process = await launcher.BuildProcessAsync(version.Name, new MLaunchOption
        {
            Session = MSession.CreateOfflineSession("Steve"),
            GameLauncherName = "NyxLauncher",
            ArgumentDictionary = new Dictionary<string, string> { ["game_directory"] = gameDirectory }
        }, token);
        process.StartInfo.WorkingDirectory = gameDirectory;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        return process;
    }
}
