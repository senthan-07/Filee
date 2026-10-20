// Optional engine downloads without the network: a fake HTTP handler serves generated archives.

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Filee.Engines.Infrastructure;
using SharpCompress.Compressors.ZStandard;

namespace Filee.Engines.Tests;

public sealed class EngineInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "filee-installer-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void A_separate_data_folder_keeps_its_own_engines()
    {
        Assert.Equal(Path.Combine(_root, "engines"), EngineEnvironment.DefaultDownloadRoot(_root));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Filee", "engines"),
            EngineEnvironment.DefaultDownloadRoot(null));
    }

    /// <summary>Serves byte arrays by URL; a string value is a redirect target.</summary>
    private sealed class FakeServer(Dictionary<string, object> content) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            var response = content.GetValueOrDefault(url) switch
            {
                byte[] bytes => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) },
                string location => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            return Task.FromResult(response);
        }
    }

    private static byte[] Zip(params (string Path, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(text);
            }
        }
        return buffer.ToArray();
    }

    private static byte[] TarGzip(params (string Path, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (path, text) in files)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
        }
        return buffer.ToArray();
    }

    /// <summary>A conda package: a zip with metadata and the files as a zstd-compressed tarball.</summary>
    private static byte[] Conda(params (string Path, string Text)[] files)
    {
        using var tarball = new MemoryStream();
        using (var zstd = new CompressionStream(tarball, level: 3, leaveOpen: true))
        using (var tar = new TarWriter(zstd, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (path, text) in files)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
        }

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("metadata.json").Open()))
                writer.Write("{\"conda_pkg_format_version\": 2}");
            using (var entry = zip.CreateEntry("pkg-tool-1.0-0.tar.zst").Open())
                entry.Write(tarball.ToArray());
        }
        return buffer.ToArray();
    }

    private static EngineComponent Component(string id, string kind, byte[] bytes, string? sha256 = null) =>
        new(id, "1.0", $"https://example.test/{id}.{kind}", sha256 ?? Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, kind);

    private EngineInstaller Installer(FakeServer server, params EngineComponent[] components) =>
        new(new HttpClient(server), _root, components.ToDictionary(c => c.Id));

    [Fact]
    public async Task Zip_engines_are_verified_unpacked_without_their_top_folder_and_reported()
    {
        var archive = Zip(("tool-1.0/tool.exe", "binary"), ("tool-1.0/readme.txt", "hello"));
        var component = Component("tool", "zip", archive);
        var server = new FakeServer(new() { [component.Url] = archive });
        var installer = Installer(server, component);
        var package = new EnginePackage("tool", ["tool"], 1000, ["tool"]);
        var reports = new List<EngineInstallProgress>();

        Assert.False(installer.IsInstalled(package));
        await installer.InstallAsync(package, new SyncProgress(reports.Add), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_root, "tool", "tool.exe")));
        Assert.True(installer.IsInstalled(package));
        Assert.True(installer.IsUpToDate(package));
        Assert.Contains(reports, r => r.Stage == EngineInstallStage.Unpacking);
        Assert.Equal(1.0, reports.Where(r => r.Stage == EngineInstallStage.Downloading).Max(r => r.Fraction), 3);
        // Byte counts drive the speed and time-left display.
        var last = reports.Last(r => r.Stage == EngineInstallStage.Downloading);
        Assert.Equal((archive.Length, archive.Length), ((int)last.BytesDone, (int)last.BytesTotal));
        // Nothing is left over from downloading or unpacking.
        Assert.Equal(["tool"], Directory.GetDirectories(_root).Select(Path.GetFileName).Where(n => !n!.StartsWith(".downloads", StringComparison.Ordinal)));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".downloads")));

        // An installed, up-to-date package is not downloaded again.
        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken);
        Assert.Single(server.Requests);

        installer.Uninstall(package);
        Assert.False(Directory.Exists(Path.Combine(_root, "tool")));
    }

    [Fact]
    public void Transfer_rate_averages_over_the_last_seconds_and_estimates_the_time_left()
    {
        var rate = new TransferRate(TimeSpan.FromSeconds(5));
        Assert.Null(rate.Remaining(0, 1000));

        rate.Add(TimeSpan.Zero, 0);
        rate.Add(TimeSpan.FromSeconds(0.2), 100); // too short to tell
        Assert.Equal(0, rate.BytesPerSecond);

        for (var second = 1; second <= 10; second++)
            rate.Add(TimeSpan.FromSeconds(second), second * 1_000_000L);
        Assert.Equal(1_000_000, rate.BytesPerSecond, 0);
        Assert.Equal(TimeSpan.FromSeconds(30), rate.Remaining(10_000_000, 40_000_000));

        // The speed follows a slower connection within the window instead of keeping the old average.
        for (var second = 11; second <= 20; second++)
            rate.Add(TimeSpan.FromSeconds(second), 10_000_000L + (second - 10) * 100_000L);
        Assert.Equal(100_000, rate.BytesPerSecond, 0);
    }

    [Fact]
    public async Task A_download_with_the_wrong_checksum_is_rejected_and_nothing_is_installed()
    {
        var archive = Zip(("tool.exe", "binary"));
        var component = Component("tool", "zip", archive, sha256: new string('0', 64));
        var installer = Installer(new FakeServer(new() { [component.Url] = archive }), component);
        var package = new EnginePackage("tool", ["tool"], 1000, ["tool"]);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(package, null, TestContext.Current.CancellationToken));

        Assert.Contains("checksum", error.Message);
        Assert.False(installer.IsInstalled(package));
        Assert.False(Directory.Exists(Path.Combine(_root, "tool")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".downloads")));
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("tar.gz")]
    public async Task An_incomplete_engine_update_preserves_the_working_install_and_its_marker(string kind)
    {
        var programs = EngineEnvironment.OwnCopyPrograms["ffmpeg"];
        byte[] Archive(params (string Path, string Text)[] files) => kind == "zip" ? Zip(files) : TarGzip(files);
        var previousArchive = Archive(($"ffmpeg-1.0/bin/{programs[0]}", "working encoder"),
            ($"ffmpeg-1.0/bin/{programs[1]}", "working probe"));
        var previousComponent = Component("ffmpeg", kind, previousArchive);
        var package = new EnginePackage("ffmpeg", ["ffmpeg"], 1, ["ffmpeg"]);
        var previousInstaller = Installer(new FakeServer(new() { [previousComponent.Url] = previousArchive }), previousComponent);
        await previousInstaller.InstallAsync(package, null, TestContext.Current.CancellationToken);

        var incompleteArchive = Archive(($"ffmpeg-2.0/bin/{programs[0]}", "new encoder without probe"));
        var incompleteComponent = Component("ffmpeg", kind, incompleteArchive) with { Version = "2.0" };
        var installer = Installer(new FakeServer(new() { [incompleteComponent.Url] = incompleteArchive }), incompleteComponent);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.InstallAsync(package, null, TestContext.Current.CancellationToken));

        Assert.Contains("ffmpeg", error.Message);
        var folder = Path.Combine(_root, "ffmpeg");
        Assert.Equal("working encoder", File.ReadAllText(Path.Combine(folder, "bin", programs[0])));
        Assert.Equal("working probe", File.ReadAllText(Path.Combine(folder, "bin", programs[1])));
        Assert.Equal(previousComponent.Sha256, File.ReadAllText(Path.Combine(folder, ".filee-component")));
        Assert.True(previousInstaller.IsUpToDate(package));
        Assert.True(installer.IsInstalled(package));
        Assert.False(installer.IsUpToDate(package));
        Assert.Empty(Directory.GetDirectories(_root, "~*"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".downloads")));
    }

    [Theory]
    [InlineData("libreoffice", "LibreOffice.app", "soffice")]
    [InlineData("calibre", "calibre.app", "ebook-convert")]
    public void A_Mac_application_bundle_requires_its_conversion_program(string id, string app, string program)
    {
        var folder = Path.Combine(_root, app, "Contents", "MacOS");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "README"), "not an executable");

        Assert.Throws<InvalidDataException>(() => EngineInstaller.RequiredProgramFolder(id, _root, windows: false));

        File.WriteAllText(Path.Combine(folder, program), "binary");
        Assert.Equal(folder, EngineInstaller.RequiredProgramFolder(id, _root, windows: false));
    }

    [Fact]
    public async Task Redirects_to_plain_http_mirrors_are_followed_because_the_hash_is_checked()
    {
        var archive = Zip(("tool.exe", "binary"));
        var component = Component("tool", "zip", archive);
        var server = new FakeServer(new()
        {
            [component.Url] = "https://example.test/mirror-list",
            ["https://example.test/mirror-list"] = "http://mirror.example.test/tool.zip",
            ["http://mirror.example.test/tool.zip"] = archive,
        });
        var installer = Installer(server, component);

        await installer.InstallAsync(new EnginePackage("tool", ["tool"], 1, ["tool"]), null, TestContext.Current.CancellationToken);

        Assert.Equal(3, server.Requests.Count);
        Assert.True(File.Exists(Path.Combine(_root, "tool", "tool.exe")));
    }

    [Fact]
    public async Task Conda_packages_unpack_their_windows_binaries_and_get_the_runtime_next_to_them()
    {
        var gs = Conda(("Library/bin/gswin64c.exe", "exe"), ("Library/bin/gsdll64.dll", "dll"), ("Library/share/doc.txt", "doc"), ("info/index.json", "{}"));
        var runtime = Conda(("Library/bin/vcruntime140.dll", "runtime"), ("Library/bin/msvcp140.dll", "c++"));
        var ghostscript = Component("ghostscript", "conda", gs);
        var vcruntime = Component("vcruntime", "conda", runtime);
        var installer = Installer(new FakeServer(new() { [ghostscript.Url] = gs, [vcruntime.Url] = runtime }), ghostscript, vcruntime);
        var package = new EnginePackage("ghostscript", ["ghostscript", "vcruntime"], 1, ["ghostscript"]);

        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken);

        var folder = Path.Combine(_root, "ghostscript");
        Assert.Equal(["gsdll64.dll", "gswin64c.exe", "msvcp140.dll", "vcruntime140.dll"],
            Directory.GetFiles(folder).Select(Path.GetFileName).Where(n => !n!.StartsWith('.')).Order(StringComparer.Ordinal));
        Assert.Equal("runtime", File.ReadAllText(Path.Combine(folder, "vcruntime140.dll")));
        Assert.True(installer.IsInstalled(package));
    }

    [Fact]
    public void Conda_packages_cannot_write_outside_the_engine_folder()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "evil.conda");
        File.WriteAllBytes(file, Conda(("Library/bin/../../../evil.txt", "evil")));

        Assert.Throws<InvalidDataException>(() => EngineInstaller.UnpackConda(file, Path.Combine(_root, "target"), CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public async Task LibreOffice_extensions_go_into_the_installed_LibreOffice_and_need_it()
    {
        var oxt = Zip(("description.xml", "<description/>"), ("H2Orestart.jar", "jar"));
        var extension = Component("h2orestart", "oxt", oxt);
        var server = new FakeServer(new() { [extension.Url] = oxt });
        var installer = Installer(server, extension);
        var package = new EnginePackage("filter", ["h2orestart"], 1, []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(package, null, TestContext.Current.CancellationToken));

        Directory.CreateDirectory(Path.Combine(_root, "libreoffice", "share", "extensions"));
        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(_root, "libreoffice", "share", "extensions", "H2Orestart", "H2Orestart.jar")));
        Assert.True(installer.IsInstalled(package));
    }

    [Fact]
    public void LibreOffice_is_trimmed_to_what_headless_conversion_needs()
    {
        var root = Path.Combine(_root, "lo");
        foreach (var folder in new[] { "help", "readmes", "program", "share/gallery", "share/extensions/dict-en", "share/extensions/dict-ko", "share/extensions/dict-fr", "share/extensions/H2Orestart" })
            Directory.CreateDirectory(Path.Combine(root, folder));
        File.WriteAllText(Path.Combine(root, "LibreOffice.msi"), "");

        EngineInstaller.TrimLibreOffice(root);

        var remaining = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(d => Path.GetRelativePath(root, d).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["program", "share", "share/extensions", "share/extensions/H2Orestart", "share/extensions/dict-en", "share/extensions/dict-ko"], remaining);
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public void Every_package_component_is_pinned_in_the_manifest()
    {
        foreach (var package in EngineDownloads.Packages.Where(EngineDownloads.CanDownload))
        {
            foreach (var id in package.Components)
            {
                var component = EngineDownloads.Components[id];
                Assert.StartsWith("https://", component.Url);
                Assert.Matches("^[0-9A-F]{64}$", component.Sha256);
                Assert.True(component.Size > 0);
            }
            Assert.True(EngineDownloads.DownloadSize(package) > 0);
        }
        // rhwp is bundled with the installer, but its download is pinned in the same list.
        Assert.Contains("rhwp", EngineDownloads.Components.Keys);
    }

    [Fact]
    public async Task A_single_file_is_copied_into_a_folder_of_its_own()
    {
        var model = Encoding.UTF8.GetBytes("language model");
        var component = new EngineComponent("tessdata-eng", "4.1.0", "https://example.test/tessdata/eng.traineddata",
            Convert.ToHexString(SHA256.HashData(model)), model.Length, "file");
        var installer = Installer(new FakeServer(new() { [component.Url] = model }), component);
        var package = new EnginePackage("tessdata", ["tessdata-eng"], 1, []);

        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken);

        Assert.Equal("language model", File.ReadAllText(Path.Combine(_root, "tessdata-eng", "eng.traineddata")));
        Assert.True(installer.IsUpToDate(package));
        installer.Uninstall(package);
        Assert.False(Directory.Exists(Path.Combine(_root, "tessdata-eng")));
    }

    [Fact]
    public async Task An_nsis_installer_is_opened_with_7zip_and_only_the_program_and_its_libraries_are_kept()
    {
        Assert.SkipUnless(EngineEnvironment.BundledProgram("7zip", EngineEnvironment.OwnCopyPrograms["7zip"][0]) is not null,
            "7-Zip is not available (pwsh build/fetch-engines.ps1 -Only 7zip).");
        // 7-Zip opens any archive by its content; a zip stands in for the installer's NSIS data.
        var installer = Zip(("tesseract.exe", "program"), ("libtesseract-5.dll", "library"), ("lstmtraining.exe", "training tool"),
            ("tesseract-uninstall.exe", "uninstaller"), ("doc/LICENSE", "Apache-2.0"), ("doc/README.md", "readme"),
            ("tessdata/pdf.ttf", "font"), ("$PLUGINSDIR/System.dll", "installer plug-in"));
        var component = Component("tesseract", "nsis", installer);
        var package = new EnginePackage("tesseract", ["tesseract"], 1, []);
        var downloader = Installer(new FakeServer(new() { [component.Url] = installer }), component);

        await downloader.InstallAsync(package, null, TestContext.Current.CancellationToken);

        var folder = Path.Combine(_root, "tesseract");
        Assert.Equal(["doc/LICENSE", "libtesseract-5.dll", "tesseract.exe"],
            Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).Where(n => !n.StartsWith('.')).Order(StringComparer.Ordinal));
        Assert.True(downloader.IsUpToDate(package));
        // Nothing is left over from unpacking.
        Assert.Equal(["tesseract"], Directory.GetDirectories(_root).Select(Path.GetFileName).Where(n => !n!.StartsWith(".downloads", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task An_installer_without_the_program_is_refused_and_nothing_is_installed()
    {
        Assert.SkipUnless(EngineEnvironment.BundledProgram("7zip", EngineEnvironment.OwnCopyPrograms["7zip"][0]) is not null,
            "7-Zip is not available (pwsh build/fetch-engines.ps1 -Only 7zip).");
        var installer = Zip(("libtesseract-5.dll", "library only"));
        var component = Component("tesseract", "nsis", installer);
        var package = new EnginePackage("tesseract", ["tesseract"], 1, []);
        var downloader = Installer(new FakeServer(new() { [component.Url] = installer }), component);

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.InstallAsync(package, null, TestContext.Current.CancellationToken));

        Assert.False(downloader.IsInstalled(package));
        Assert.False(Directory.Exists(Path.Combine(_root, "tesseract")));
    }

    [Fact]
    public void Tesseract_is_a_pinned_windows_download_from_the_projects_own_releases_and_a_system_install_elsewhere()
    {
        var windows = EngineDownloads.ComponentsForRuntime("win-x64");
        var program = windows["tesseract"];
        var model = windows["tessdata-eng"];
        Assert.StartsWith("https://github.com/tesseract-ocr/tesseract/releases/download/", program.Url);
        Assert.StartsWith("https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/", model.Url);
        Assert.Equal(("nsis", "file"), (program.Kind, model.Kind));
        foreach (var component in new[] { program, model })
        {
            Assert.Matches("^[0-9A-F]{64}$", component.Sha256);
            Assert.True(component.Size > 0);
        }

        // No download for the other systems: they use Tesseract from the package manager (like Ghostscript).
        foreach (var runtime in new[] { "linux-x64", "linux-arm64", "osx-x64", "osx-arm64" })
            Assert.DoesNotContain("tesseract", EngineDownloads.ComponentsForRuntime(runtime).Keys);
        var package = Assert.Single(EngineDownloads.Packages, p => p.Id == "tesseract");
        Assert.Equal(["tesseract", "tessdata-eng"], package.Components);
        // It is for scanned PDFs only, so neither the installer nor the first run ticks it.
        Assert.False(EngineDownloads.IsSuggested(package));
    }

    /// <summary>IProgress that reports synchronously (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class SyncProgress(Action<EngineInstallProgress> report) : IProgress<EngineInstallProgress>
    {
        public void Report(EngineInstallProgress value) => report(value);
    }
}
