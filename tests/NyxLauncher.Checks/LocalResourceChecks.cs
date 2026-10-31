using System.IO.Compression;
using NyxLauncher.Services;

internal static class LocalResourceChecks
{
    /// <summary>用独立目录验证资源识别、加载器筛选、隔离路径、存档归属及同名保护；可选测试目录用于读取真实样本，不修改源文件。</summary>
    public static async Task RunAsync(string? samples = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "nyx-local-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            foreach (var (category, entries) in new[] {
                ("resourcepack", new[] { "pack.mcmeta", "assets/a" }),
                ("datapack", new[] { "pack.mcmeta", "data/a" }),
                ("shader", new[] { "shaders/a" }), ("mod", new[] { "fabric.mod.json" }),
                ("modpack", new[] { "instance/mmc-pack.json" }) })
            {
                string path = Path.Combine(root, category + ".zip");
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                    foreach (string entry in entries) using (var writer = new StreamWriter(zip.CreateEntry(entry).Open())) writer.Write("{}");
                var resource = LocalResource.Read(path);
                if (resource.Category != category) throw new Exception("资源识别错误：" + category);
                string destination = Path.Combine(root, "output", category);
                await resource.CopyAsync(destination);
                await resource.CopyAsync(destination);
                string installed = Path.Combine(destination, Path.GetFileName(path));
                if (!File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(installed))) throw new Exception("复制内容错误");
                File.WriteAllText(installed, "existing");
                try { await resource.CopyAsync(destination); throw new Exception("覆盖保护失效"); }
                catch (IOException) { }
                if (File.ReadAllText(installed) != "existing") throw new Exception("已有内容被破坏");
            }
            string versionPath = Path.Combine(root, "versions", "A");
            Directory.CreateDirectory(versionPath);
            File.WriteAllText(Path.Combine(versionPath, "A.json"), """{"id":"A","clientVersion":"26.3","type":"release"}""");
            var version = InstalledVersionService.Scan(root).Single();
            var pack = LocalResource.Read(Path.Combine(root, "resourcepack.zip"));
            if (pack.Resolve(root, version, IsolationMode.All, null) != Path.Combine(versionPath, "resourcepacks")) throw new Exception("隔离路径错误");
            if (pack.Resolve(root, version, IsolationMode.Disabled, null) != Path.Combine(root, "resourcepacks")) throw new Exception("共享路径错误");
            var mod = LocalResource.Read(Path.Combine(root, "mod.zip"));
            if (mod.Supports(version) || !mod.Supports(version with { Loader = "Fabric: 0.18" })) throw new Exception("加载器筛选错误");
            string world = Path.Combine(versionPath, "saves", "world");
            Directory.CreateDirectory(world);
            File.WriteAllText(Path.Combine(world, "level.dat"), "fixture");
            var data = LocalResource.Read(Path.Combine(root, "datapack.zip"));
            if (data.Resolve(root, version, IsolationMode.All, world) != Path.Combine(world, "datapacks")) throw new Exception("存档路径错误");
            File.Delete(Path.Combine(world, "level.dat"));
            try { data.Resolve(root, version, IsolationMode.All, world); throw new Exception("接受已删除存档"); }
            catch (IOException) { }
            File.WriteAllText(Path.Combine(root, "bad.zip"), "bad archive");
            try { LocalResource.Read(Path.Combine(root, "bad.zip")); throw new Exception("接受损坏归档"); }
            catch (InvalidDataException) { }
            if (samples is not null)
                foreach (var (name, category) in new[] { ("Fast Better Grass.zip", "resourcepack"), ("furnicraft-v7.5.zip", "datapack"), ("xaerominimap-fabric-26.3-26.6.2.jar", "mod") })
                {
                    var resource = LocalResource.Read(Path.Combine(samples, name));
                    if (resource.Category != category) throw new Exception("真实样本识别错误：" + name);
                    await resource.CopyAsync(Path.Combine(root, "samples"));
                    if (!File.ReadAllBytes(resource.Path).SequenceEqual(File.ReadAllBytes(Path.Combine(root, "samples", name)))) throw new Exception("真实样本复制错误");
                }
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("本地资源识别、复制、同名保护与目标边界检查通过。");
    }
}
