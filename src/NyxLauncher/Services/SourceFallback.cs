using System.Text.Json;

namespace NyxLauncher.Services;

/// <summary>游戏与加载器清单共用的双源顺序、慢源回退和取消规则。</summary>
internal static class SourceFallback
{
    /// <summary>调用指定来源的读取器（true 为镜像），返回首个有效结果；双源失败汇总错误，退出时取消并观察剩余请求。</summary>
    public static async Task<T> FetchAsync<T>(SourceStrategy strategy, Func<bool, CancellationToken, Task<T>> fetch,
        TimeSpan fallbackDelay, CancellationToken cancellationToken)
    {
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bool firstMirror = strategy == SourceStrategy.MirrorFirst;
        var tasks = new List<Task<T>> { fetch(firstMirror, pending.Token) };
        var errors = new List<string>();
        bool secondStarted = false;
        try
        {
            if (strategy == SourceStrategy.OfficialWithFallback &&
                await Task.WhenAny(tasks[0], Task.Delay(fallbackDelay, pending.Token)) != tasks[0])
            {
                cancellationToken.ThrowIfCancellationRequested();
                tasks.Add(fetch(!firstMirror, pending.Token));
                secondStarted = true;
            }
            while (tasks.Count > 0)
            {
                var finished = await Task.WhenAny(tasks);
                tasks.Remove(finished);
                try { return await finished; }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    errors.Add(ex.Message);
                    if (!secondStarted)
                    {
                        tasks.Add(fetch(!firstMirror, pending.Token));
                        secondStarted = true;
                    }
                }
            }
            throw new HttpRequestException("官方源与镜像源均无法加载。\n" + string.Join("\n", errors));
        }
        finally
        {
            pending.Cancel();
            foreach (var task in tasks)
            {
                try { await task; }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or OperationCanceledException) { }
            }
        }
    }
}
