// Keyboard behaviour of the chip input (TagEditor) and the profile editing of the donut toolbar page,
// driven through a headless window.

using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.App.Views.Pages;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;
using Filee.Engines.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

public class TagEditorTests
{
    private readonly ToolbarProfile _profile = new() { Id = "mine", Name = "Mine", Extensions = ["png"] };

    private (Window Window, TagEditor Editor, ExtensionTagsViewModel Vm) Show()
    {
        TestServices.EnsureInitialized("en");
        var vm = new ExtensionTagsViewModel(_profile, [_profile], new KeyLocalizer(), () => { });
        var editor = new TagEditor { AddCommand = vm.AddCommand, RemoveCommand = vm.RemoveCommand, DataContext = vm };
        editor.Bind(TagEditor.TagsProperty, new ReflectionBinding(nameof(vm.Tags)));
        editor.Bind(TagEditor.SuggestionsProperty, new ReflectionBinding(nameof(vm.Suggestions)));
        editor.Bind(TagEditor.TextProperty, new ReflectionBinding(nameof(vm.Text)) { Mode = BindingMode.TwoWay });
        var window = new Window { Width = 500, Height = 400, Content = new StackPanel { Children = { editor, new TextBox() } } };
        window.Show();
        Pump();
        editor.FocusInput();
        Pump();
        return (window, editor, vm);
    }

    private static TextBox Input(TagEditor editor) => editor.GetVisualDescendants().OfType<TextBox>().First();

    [AvaloniaFact]
    public void Enter_turns_the_text_into_a_chip()
    {
        var (window, editor, vm) = Show();
        window.KeyTextInput(".JPG");
        Press(window, Key.Enter);

        Assert.Equal(["png", "jpg"], _profile.Extensions);
        Assert.Equal("", Input(editor).Text);
        Assert.Equal("", vm.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Separators_and_pasted_lists_add_several_chips()
    {
        var (window, editor, _) = Show();
        window.KeyTextInput("gif,");
        Assert.Equal(["png", "gif"], _profile.Extensions);

        window.KeyTextInput("webp tif");
        Assert.Equal(["png", "gif", "webp"], _profile.Extensions);
        Assert.Equal("tif", Input(editor).Text); // unfinished: stays in the box

        // A pasted list with line breaks and wildcards.
        Input(editor).Text = "";
        window.KeyTextInput("*.bmp;\r\n*.TGA\n");
        Assert.Equal(["png", "gif", "webp", "bmp", "tga"], _profile.Extensions);
        window.Close();
    }

    [AvaloniaFact]
    public void Tab_adds_the_text_but_still_moves_on_from_an_empty_box()
    {
        var (window, editor, _) = Show();
        window.KeyTextInput("avif");
        Press(window, Key.Tab);
        Assert.Equal(["png", "avif"], _profile.Extensions);
        Assert.True(Input(editor).IsFocused);

        Press(window, Key.Tab);
        Assert.False(Input(editor).IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void Backspace_in_the_empty_box_removes_the_last_chip()
    {
        var (window, _, vm) = Show();
        window.KeyTextInput("gif");
        Press(window, Key.Enter);
        Press(window, Key.Back);

        Assert.Equal(["png"], _profile.Extensions);
        Assert.Equal([".png"], vm.Tags.Select(t => t.Text));
        window.Close();
    }

    [AvaloniaFact]
    public void Arrow_keys_pick_a_suggestion()
    {
        var (window, editor, _) = Show();
        window.KeyTextInput("jp");
        Pump();
        Assert.True(editor.IsSuggestionListOpen);

        Press(window, Key.Down);
        Press(window, Key.Down);
        Press(window, Key.Enter);

        Assert.Equal(["png", "jpeg"], _profile.Extensions);
        Assert.Equal("", Input(editor).Text);
        Assert.False(editor.IsSuggestionListOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void Escape_closes_the_suggestions_then_clears_the_text()
    {
        var (window, editor, _) = Show();
        window.KeyTextInput("jp");
        Pump();
        Press(window, Key.Escape);
        Assert.False(editor.IsSuggestionListOpen);
        Assert.Equal("jp", Input(editor).Text);

        Press(window, Key.Escape);
        Assert.Equal("", Input(editor).Text);
        Assert.Equal(["png"], _profile.Extensions);
        window.Close();
    }

    [AvaloniaFact]
    public void Chips_can_be_reached_and_removed_with_the_keyboard()
    {
        var (window, editor, _) = Show();
        window.KeyTextInput("gif,webp,");
        Press(window, Key.Left);  // into the last chip (.webp)
        Press(window, Key.Left);  // .gif
        Press(window, Key.Delete);
        Pump();

        Assert.Equal(["png", "webp"], _profile.Extensions);
        Assert.False(Input(editor).IsFocused); // focus stays among the chips
        Press(window, Key.Escape);
        Assert.True(Input(editor).IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void Leaving_the_box_keeps_what_was_typed()
    {
        var (window, editor, _) = Show();
        window.KeyTextInput("heic");
        ((TextBox)((StackPanel)window.Content!).Children[1]).Focus();
        Pump();

        Assert.Equal(["png", "heic"], _profile.Extensions);
        Assert.Equal("", Input(editor).Text);
        window.Close();
    }

    private static void Press(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}

public class ToolbarPageTests
{
    [AvaloniaFact]
    public void Profiles_switch_between_extension_chips_and_the_mixed_check_list()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var snapshot = store.Profiles.Select(p => p.Clone()).ToList();
        try
        {
            using var vm = new ToolbarPageViewModel(store, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<WindowService>(),
                AppHost.Get<PresetAvailability>(), AppHost.Get<ConverterCatalog>(), NullLogger<ToolbarPageViewModel>.Instance);

            // A new profile goes before the mixed ones and starts as a normal profile.
            vm.AddProfileCommand.Execute(null);
            var item = vm.SelectedProfile!;
            var mixedIndex = store.Profiles.FindIndex(p => p.IsFallback);
            Assert.Equal(mixedIndex - 1, store.Profiles.IndexOf(item.Profile));
            Assert.NotNull(vm.ExtensionTags);
            Assert.Null(vm.MixedExtensions);
            vm.ExtensionTags!.AddCommand.Execute("jpg");

            // Made mixed: it moves behind the built-in "Mixed files" (which stays the catch-all) and keeps its
            // extensions as checks.
            vm.IsMixed = true;
            Assert.Same(item, vm.SelectedProfile);
            Assert.True(item.Profile.IsFallback);
            Assert.True(item.IsMixed);
            Assert.Same(item.Profile, store.Profiles[^1]);
            Assert.Equal(store.Profiles.Select(p => p.Id), vm.Profiles.Select(p => p.Profile.Id));
            Assert.Null(vm.ExtensionTags);
            Assert.NotNull(vm.MixedExtensions);
            Assert.True(vm.MixedExtensions!.Groups.Single(g => g.Name == "Images").State is null);
            Assert.Equal("mixed", ProfileSelector.CatchAll(store.Profiles)!.Id);
            Assert.True(vm.CanDelete);

            // And back.
            vm.IsMixed = false;
            Assert.False(item.Profile.IsFallback);
            Assert.Equal(store.Profiles.FindIndex(p => p.IsFallback) - 1, store.Profiles.IndexOf(item.Profile));
            Assert.Equal(["jpg"], item.Profile.Extensions);
        }
        finally
        {
            store.Profiles.Clear();
            store.Profiles.AddRange(snapshot);
            store.SaveLibrary();
        }
    }

    [AvaloniaFact]
    public void A_new_group_heads_the_available_presets_and_every_profile_count_stays_right()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var presets = store.Presets.ToList();
        var profiles = store.Profiles.Select(p => p.Clone()).ToList();
        try
        {
            using var vm = new ToolbarPageViewModel(store, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<WindowService>(),
                AppHost.Get<PresetAvailability>(), AppHost.Get<ConverterCatalog>(), NullLogger<ToolbarPageViewModel>.Instance);
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "images");
            vm.PaletteSearch = "png"; // a search that would hide it
            var pdf = vm.Profiles.Single(p => p.Profile.Id == "pdf");
            pdf.Profile.PresetIds.RemoveAt(0); // changed elsewhere, as when a deleted group leaves every donut

            vm.AddGroup(new Filee.Core.Presets.Preset
            {
                Id = "test-new-group",
                Name = "Web",
                Kind = Filee.Core.Presets.PresetKind.Group,
                SubPresetIds = ["to-png"],
            });
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("", vm.PaletteSearch);
            // Up with the groups at the top, above every conversion: not below ~80 presets.
            var chips = vm.VisiblePalette.Select(c => store.FindPreset(c.PresetId)!).ToList();
            var at = chips.FindIndex(p => p.Id == "test-new-group");
            Assert.True(at >= 0 && chips.Take(at).All(p => p.IsGroup), $"at {at}");
            Assert.Equal($"{pdf.Profile.PresetIds.Count}", pdf.Count);
            Assert.DoesNotContain("test-new-group", vm.SelectedProfile!.Profile.PresetIds); // on no donut until dragged there
        }
        finally
        {
            store.Presets.Clear();
            store.Presets.AddRange(presets);
            store.Profiles.Clear();
            store.Profiles.AddRange(profiles);
            store.SaveLibrary();
        }
    }

    [AvaloniaFact]
    public void Reset_puts_a_built_in_profile_back_and_the_list_follows()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var presets = store.Presets.ToList();
        var profiles = store.Profiles.Select(p => p.Clone()).ToList();
        try
        {
            using var vm = new ToolbarPageViewModel(store, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<WindowService>(),
                AppHost.Get<PresetAvailability>(), AppHost.Get<ConverterCatalog>(), NullLogger<ToolbarPageViewModel>.Instance);
            var item = vm.Profiles.Single(p => p.Profile.Id == "images");
            vm.SelectedProfile = item;
            Assert.True(vm.CanReset);
            vm.ProfileName = "Pictures";
            vm.RemoveAt(0);
            vm.IsMixed = true; // moves behind "Mixed files"
            Assert.Same(item.Profile, store.Profiles[^1]);

            vm.Reset(item);

            var builtIn = Filee.Core.Presets.BuiltInData.CreateProfiles().Single(p => p.Id == "images");
            Assert.Same(item, vm.SelectedProfile);
            Assert.Equal("Images", item.Name);
            Assert.Equal("Images", vm.ProfileName);
            Assert.Equal("Images", vm.CenterTitle);
            Assert.False(vm.IsMixed);
            Assert.NotNull(vm.ExtensionTags);
            Assert.Equal(builtIn.PresetIds, vm.DonutItems.Select(d => d.Id));
            Assert.Equal($"{builtIn.PresetIds.Count}", item.Count);
            Assert.Equal(store.Profiles.Select(p => p.Id), vm.Profiles.Select(p => p.Profile.Id));
            Assert.Equal(store.Profiles.FindIndex(p => p.IsFallback) - 1, store.Profiles.IndexOf(item.Profile));

            // A new profile has nothing to go back to.
            vm.AddProfileCommand.Execute(null);
            Assert.False(vm.CanReset);
            Assert.True(vm.CanDelete);
            var mine = vm.SelectedProfile!;
            vm.Reset(mine);
            Assert.Equal("New profile", mine.Name);
        }
        finally
        {
            store.Presets.Clear();
            store.Presets.AddRange(presets);
            store.Profiles.Clear();
            store.Profiles.AddRange(profiles);
            store.SaveLibrary();
        }
    }

    [AvaloniaFact]
    public void The_last_mixed_profile_stays_mixed()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        using var vm = new ToolbarPageViewModel(store, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<WindowService>(),
            AppHost.Get<PresetAvailability>(), AppHost.Get<ConverterCatalog>(), NullLogger<ToolbarPageViewModel>.Instance);
        vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "mixed");

        Assert.True(vm.IsMixed);
        Assert.False(vm.CanChangeMixed);
        Assert.False(vm.CanDelete);
        vm.IsMixed = false;

        Assert.True(store.Profiles.Single(p => p.Id == "mixed").IsFallback);
        Assert.True(vm.IsMixed);
        Assert.NotNull(vm.MixedExtensions);
    }
}

public class PresetAvailabilityTests
{
    private class FakeConverter(string id, IReadOnlyList<ConversionEdge> edges) : IConverter
    {
        public FakeConverter(string id, params (string From, string To)[] edges)
            : this(id, edges.Select(e => new ConversionEdge(e.From, e.To)).ToList()) { }

        public string Id => id;
        public string DisplayName => id;
        public IReadOnlyList<ConversionEdge> Edges => edges;
        public int MaxParallelism => 1;
        public bool Available = true;
        public int StatusCalls;
        public EngineStatus GetStatus()
        {
            StatusCalls++;
            return Available ? EngineStatus.Available() : EngineStatus.Unavailable("engine.reason.not_found");
        }
        public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeTransformer(string id, IReadOnlyList<ConversionEdge> edges)
        : FakeConverter(id, edges), IImageTransformer
    {
        public bool Transforms(string from, string to) => true;
    }

    private sealed class ThrowingCombiner() : FakeConverter("test-throwing-combiner", Array.Empty<ConversionEdge>()), IFileCombiner
    {
        public bool CanCombineInto(string format) => throw new InvalidOperationException("broken combiner");

        public Task CombineAsync(IReadOnlyList<string> inputPaths, string outputPath, string format, Preset preset,
            string workDirectory, IProgress<double>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class PaletteLogger : ILogger<ToolbarPageViewModel>
    {
        public readonly List<(LogLevel Level, Exception? Exception, string Message)> Warnings = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add((logLevel, exception, formatter(state, exception)));
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly UserDataStore _store;
        private readonly List<Preset> _presets;
        private readonly List<ToolbarProfile> _profiles;
        public readonly FakeConverter Built = new("test-built", new ConversionEdge[] { new("png", "jpg"), new("png", "png", Cost: 10) });
        public readonly FakeConverter Gated = new("ffmpeg", ("png", "mp4")) { Available = false };
        public readonly FakeConverter Motion = new("test-motion", new ConversionEdge[] { new("jpg", "mp4", MotionPhoto: true) });
        public readonly FakeTransformer Turner = new("pandoc", [new("png", "png", Cost: 1)]) { Available = false };
        public readonly ConverterCatalog Catalog;
        public readonly PresetAvailability Availability;
        public readonly ILocalizer Loc;
        public readonly List<EnginePackageState> Packages;

        public Fixture(IConverter? extra = null)
        {
            TestServices.EnsureInitialized("en");
            _store = AppHost.Get<UserDataStore>();
            _presets = _store.Presets.ToList();
            _profiles = _store.Profiles.Select(p => p.Clone()).ToList();
            Loc = AppHost.Get<ILocalizer>();
            var converters = new List<IConverter> { Built, Gated, Motion, Turner };
            if (extra is not null)
                converters.Add(extra);
            Catalog = new ConverterCatalog(converters);
            Packages = EngineDownloads.Packages.Where(p => p.Id is "pandoc" or "ffmpeg")
                .Select(p => new EnginePackageState(p, Loc) { Status = EnginePackageStatus.NotInstalled }).ToList();
            Availability = new PresetAvailability(Catalog, Loc, Packages);
            _store.Presets.AddRange(
            [
                new() { Id = "test-jpg", TargetFormat = "jpg" },
                new() { Id = "test-mp4", TargetFormat = "mp4" },
                new() { Id = "test-wav", TargetFormat = "wav" },
                new() { Id = "test-rotate-png", TargetFormat = "png", Image = new() { Rotation = ImageRotation.Right90 } },
                new() { Id = "test-motion", TargetFormat = "mp4", Media = new() { ExtractMotionPhoto = true } },
                new() { Id = "test-group", Kind = PresetKind.Group, SubPresetIds = ["test-wav"] },
                new() { Id = "test-pack", TargetFormat = "zip", Archive = new() { CombineIntoOne = true } },
                new() { Id = "test-merge", TargetFormat = "pdf", Pdf = new() { MergeIntoSingle = true } },
            ]);
        }

        public Preset Preset(string id) => _store.FindPreset(id)!;
        public UserDataStore Store => _store;
        public ToolbarPageViewModel Page(ToolbarProfile profile, ILogger<ToolbarPageViewModel>? log = null)
        {
            _store.Profiles.Add(profile);
            var vm = new ToolbarPageViewModel(_store, Loc, AppHost.Get<WindowService>(), Availability, Catalog,
                log ?? NullLogger<ToolbarPageViewModel>.Instance);
            vm.SelectedProfile = vm.Profiles.Single(p => ReferenceEquals(p.Profile, profile));
            return vm;
        }
        public ToolbarPageViewModel PageWithPalette(params string[] extensions) => Page(new ToolbarProfile
        {
            Id = "test-palette",
            Extensions = [.. extensions],
            PresetIds = [],
            BackPresetIds = [],
        });
        public static FileFormat Format(string id) => FormatRegistry.Get(id);

        public void Dispose()
        {
            _store.Presets.Clear();
            _store.Presets.AddRange(_presets);
            _store.Profiles.Clear();
            _store.Profiles.AddRange(_profiles);
            _store.SaveLibrary();
        }
    }

    [AvaloniaFact]
    public void Check_keeps_its_answers()
    {
        using var f = new Fixture();
        var png = Fixture.Format("png");
        var mp4 = Fixture.Format("mp4");
        var msrc = Fixture.Format(MediaOptions.MotionPhotoSources[0]);
        Preset P(string id) => f.Preset(id);
        string? Check(string id, params FileFormat[] formats) => f.Availability.Check(P(id), formats);

        Assert.Null(Check("tool-crop", png));
        Assert.Equal(f.Loc["error.unsupported_source"], Check("tool-crop", png, mp4));
        Assert.Equal(f.Loc["error.unsupported_source"], Check("test-jpg"));
        Assert.Equal(f.Loc["error.no_route"], Check("test-pack"));
        Assert.Null(Check("test-jpg", png));
        Assert.Equal(f.Loc.Format("engine.reason.needs_package", f.Packages.Single(p => p.Package.Id == "ffmpeg").Name),
            Check("test-mp4", png));
        Assert.Equal(f.Loc["error.no_route"], Check("test-wav", png));
        Assert.Equal(f.Loc["error.cannot_turn"], Check("test-rotate-png", png));
        Assert.Null(Check("test-jpg", mp4, png));
        Assert.Null(Check("test-motion", msrc));
        Assert.Equal(f.Loc["error.no_route"], Check("test-mp4", msrc));
        Assert.Null(Check("test-merge", Fixture.Format("pdf")));
    }

    [AvaloniaFact]
    public void The_palette_session_matches_check_and_sorts_the_reasons()
    {
        using var f = new Fixture();
        var png = Fixture.Format("png");
        var session = f.Availability.ForPalette([png]);
        Assert.Equal(new PresetJudgement(PresetUse.Usable, null), session.Judge(f.Preset("test-jpg")));
        foreach (var (id, use) in new[]
        {
            ("test-mp4", PresetUse.NeedsPackage),
            ("test-wav", PresetUse.Unusable),
            ("test-rotate-png", PresetUse.Unusable),
            ("test-pack", PresetUse.Unusable),
        })
            Assert.Equal(new PresetJudgement(use, f.Availability.Check(f.Preset(id), [png])), session.Judge(f.Preset(id)));

        Assert.Equal(new PresetJudgement(PresetUse.Usable, null),
            f.Availability.ForPalette([png, Fixture.Format("mp4")]).Judge(f.Preset("tool-crop")));
        Assert.Equal(new PresetJudgement(PresetUse.Unusable, f.Loc["error.unsupported_source"]),
            f.Availability.ForPalette([Fixture.Format("mp4")]).Judge(f.Preset("tool-crop")));
    }

    [AvaloniaFact]
    public void A_preset_whose_judging_throws_shows_undimmed_and_the_rest_are_judged()
    {
        using var f = new Fixture(new ThrowingCombiner());
        f.Store.Presets.Add(new() { Id = "test-jpg-after", TargetFormat = "jpg" });
        f.Store.Presets.Add(new() { Id = "test-mp4-after", TargetFormat = "mp4" });
        var log = new PaletteLogger();
        using var vm = f.Page(new ToolbarProfile
        {
            Id = "test-palette",
            Extensions = ["png", "mp4"],
            PresetIds = ["zip-all"],
            BackPresetIds = [],
        }, log);

        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.Palette.Single(c => c.PresetId == "test-pack").Reason);
        Assert.Null(vm.Palette.Single(c => c.PresetId == "test-jpg-after").Reason);
        // Judged after the failure, not just shown: it still needs FFmpeg.
        var after = f.Availability.Check(f.Preset("test-mp4-after"), [Fixture.Format("png")]);
        Assert.NotNull(after);
        Assert.Equal(after, vm.Palette.Single(c => c.PresetId == "test-mp4-after").Reason);
        Assert.Null(vm.Palette.Single(c => c.PresetId == "test-jpg").Reason);
        Assert.Equal(f.Availability.Check(f.Preset("test-mp4"), [Fixture.Format("png")]),
            vm.Palette.Single(c => c.PresetId == "test-mp4").Reason);
        Assert.DoesNotContain(vm.Palette, c => c.PresetId == "test-wav");
        var warning = Assert.Single(log.Warnings);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.IsType<InvalidOperationException>(warning.Exception);
        Assert.Contains("test-pack", warning.Message);
        Assert.DoesNotContain(vm.Palette, c => c.PresetId == "zip-all");
    }

    [AvaloniaFact]
    public void Check_builds_no_planner_when_it_answers_early()
    {
        using var f = new Fixture();
        var png = Fixture.Format("png");
        void CheckEarly(string id, params FileFormat[] formats)
        {
            f.Catalog.Refresh();
            f.Built.StatusCalls = 0;
            f.Availability.Check(f.Preset(id), formats);
            Assert.Equal(0, f.Built.StatusCalls);
        }

        CheckEarly("tool-crop", png);
        CheckEarly("test-jpg");
        CheckEarly("test-pack");
        f.Catalog.Refresh();
        f.Built.StatusCalls = 0;
        f.Availability.Check(f.Preset("test-jpg"), [png]);
        Assert.True(f.Built.StatusCalls > 0);
    }

    [AvaloniaFact]
    public void The_palette_lists_what_the_donut_can_run_and_dims_what_needs_an_engine()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette("png", "mp4");
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Palette.Single(c => c.PresetId == "test-jpg").Reason);
        Assert.Equal(f.Availability.Check(f.Preset("test-mp4"), [Fixture.Format("png")]),
            vm.Palette.Single(c => c.PresetId == "test-mp4").Reason);
        Assert.Null(vm.Palette.Single(c => c.PresetId == "tool-crop").Reason);
        Assert.Contains(vm.Palette, c => c.PresetId == "test-group");
        foreach (var id in new[] { "test-wav", "test-rotate-png", "test-pack" })
            Assert.DoesNotContain(vm.Palette, c => c.PresetId == id);
        var firstNormal = vm.Palette.ToList().FindIndex(c => !f.Preset(c.PresetId).IsGroup);
        Assert.True(vm.Palette.Take(firstNormal).All(c => f.Preset(c.PresetId).IsGroup));
    }

    [AvaloniaFact]
    public void A_preset_on_either_face_stays_out_of_the_palette()
    {
        using var f = new Fixture();
        using var vm = f.Page(new ToolbarProfile
        {
            Id = "test-palette",
            Extensions = ["png"],
            PresetIds = ["test-jpg"],
            BackPresetIds = ["test-mp4"],
        });
        Dispatcher.UIThread.RunJobs();
        void Check()
        {
            Assert.DoesNotContain(vm.Palette, c => c.PresetId == "test-jpg" || c.PresetId == "test-mp4");
        }
        Check();
        vm.EditingBack = true;
        Dispatcher.UIThread.RunJobs();
        Check();
    }

    [AvaloniaFact]
    public void A_donut_without_extensions_shows_everything()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette("png");
        vm.AddProfileCommand.Execute(null);
        var placed = vm.SelectedProfile!.Profile.PresetIds.Concat(vm.SelectedProfile.Profile.BackPresetIds ?? []).ToHashSet();
        Assert.Empty(vm.SelectedProfile.Profile.Extensions);
        Assert.Equal(f.Store.Presets.Select(p => p.Id).Except(placed).Order(),
            vm.Palette.Select(c => c.PresetId).Order());
        Assert.All(vm.Palette, c => Assert.Null(c.Reason));
        Assert.Contains(vm.Palette, c => c.PresetId == "test-wav");
        Assert.Contains(vm.Palette, c => c.PresetId == "test-pack");
    }

    [AvaloniaFact]
    public void Editing_the_extensions_judges_the_palette_again()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette("mp4");
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(vm.Palette, c => c.PresetId == "test-jpg");
        vm.ExtensionTags!.AddCommand.Execute("png");
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(vm.Palette, c => c.PresetId == "test-jpg");
        vm.ExtensionTags.RemoveCommand.Execute(vm.ExtensionTags.Tags.Single(t => t.Value == "png"));
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(vm.Palette, c => c.PresetId == "test-jpg");

        vm.IsMixed = true;
        var png = vm.MixedExtensions!.Groups.SelectMany(g => g.Extensions).First(e => e.Extension == "png");
        png.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(vm.Palette, c => c.PresetId == "test-jpg");
        png.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(vm.Palette, c => c.PresetId == "test-jpg");
    }

    [AvaloniaFact]
    public void An_engine_change_judges_the_palette_again_until_the_page_is_closed()
    {
        using var f = new Fixture();
        var vm = f.PageWithPalette("png");
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.Palette.Single(c => c.PresetId == "test-mp4").IsDimmed);
        var changed = typeof(ConverterCatalog).GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.NotNull(changed);
        EventHandler? Subscribers() => changed.GetValue(f.Catalog) as EventHandler;
        Assert.Contains(Subscribers()!.GetInvocationList(), d => ReferenceEquals(d.Target, vm));
        f.Gated.Available = true;
        f.Catalog.Refresh();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Palette.Single(c => c.PresetId == "test-mp4").Reason);
        vm.Dispose();
        Assert.DoesNotContain(Subscribers()?.GetInvocationList() ?? [], d => ReferenceEquals(d.Target, vm));
        f.Gated.Available = false;
        f.Catalog.Refresh();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Palette.Single(c => c.PresetId == "test-mp4").Reason);
    }

    [AvaloniaFact]
    public void Switching_a_donut_to_mixed_keeps_its_palette()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette("png");
        Dispatcher.UIThread.RunJobs();
        var before = vm.Palette.Select(c => (c.PresetId, c.Reason)).ToList();
        vm.IsMixed = true;
        Assert.Equal(before, vm.Palette.Select(c => (c.PresetId, c.Reason)));
    }

    [AvaloniaFact]
    public void A_dimmed_chip_stays_dimmed_with_its_reason_and_can_still_be_added()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette("png", "mp4");
        Dispatcher.UIThread.RunJobs();
        var window = new Window { Width = 1080, Height = 820, Content = new ToolbarPage { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var chips = window.GetVisualDescendants().OfType<Border>()
            .Where(b => b.DataContext is PaletteChip).ToList();
        var dimmed = chips.Single(b => (b.DataContext as PaletteChip)!.PresetId == "test-mp4");
        var regular = chips.Single(b => (b.DataContext as PaletteChip)!.PresetId == "test-jpg");
        Assert.Contains("dimmed", dimmed.Classes);
        Assert.Equal(0.55, dimmed.Opacity);
        Assert.Equal(((PaletteChip)dimmed.DataContext!).Reason, ToolTip.GetTip(dimmed));
        Assert.True(dimmed.IsEnabled);
        Assert.DoesNotContain("dimmed", regular.Classes);
        Assert.Equal(1, regular.Opacity);
        dimmed.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!));
        Assert.Contains("test-mp4", vm.SelectedProfile!.Profile.PresetIds);
        Assert.DoesNotContain(vm.Palette, c => c.PresetId == "test-mp4");
        window.Close();
    }

    [AvaloniaFact]
    public void Editing_a_preset_judges_it_again()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette("png", "mp4");
        Dispatcher.UIThread.RunJobs();
        var preset = f.Preset("test-wav");
        Assert.DoesNotContain(vm.Palette, c => c.PresetId == preset.Id);
        preset.TargetFormat = "jpg";
        vm.PresetEdited(preset);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Palette.Single(c => c.PresetId == preset.Id).Reason);
        preset.TargetFormat = "mp4";
        vm.PresetEdited(preset);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.Palette.Single(c => c.PresetId == preset.Id).IsDimmed);
    }

    [AvaloniaFact]
    public async Task Saving_a_chip_in_the_editor_judges_it_again()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette("png", "mp4");
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.Palette.Single(c => c.PresetId == "test-mp4").IsDimmed);
        var windows = OpenMain();
        try
        {
            var editing = vm.EditChipAsync("test-mp4");
            SaveInEditor(windows, "jpg");
            await editing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(vm.Palette.Single(c => c.PresetId == "test-mp4").Reason);
        }
        finally { CloseMain(windows); }
    }

    [AvaloniaFact]
    public async Task Saving_a_slice_in_the_editor_judges_it_again()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette("png", "mp4");
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.Insert("test-mp4", 0));
        var windows = OpenMain();
        try
        {
            var editing = vm.EditAsync(0);
            SaveInEditor(windows, "wav");
            await editing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            vm.RemoveAt(0);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(vm.Palette, c => c.PresetId == "test-mp4");
        }
        finally { CloseMain(windows); }
    }

    // The editor needs a visible owner. Closing from code only hides the main window and posts the real close.
    private static WindowService OpenMain()
    {
        var windows = AppHost.Get<WindowService>();
        Assert.Null(windows.Main);
        windows.ShowMain();
        return windows;
    }

    private static void CloseMain(WindowService windows)
    {
        windows.Main?.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(windows.Main);
    }

    private static void SaveInEditor(WindowService windows, string target)
    {
        Dispatcher.UIThread.RunJobs();
        var editor = Assert.Single(windows.Main!.OwnedWindows.OfType<PresetEditorWindow>());
        var model = Assert.IsType<PresetEditorViewModel>(editor.DataContext);
        model.Target = model.Targets.Single(t => t.Value == target);
        Dispatcher.UIThread.RunJobs();
        editor.GetVisualDescendants().OfType<Button>().Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.True(editor.Saved);
    }

    [AvaloniaFact]
    public void Resetting_a_built_in_donut_judges_the_palette_again()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette("wav");
        var images = vm.Profiles.Single(p => p.Profile.Id == "images");
        images.Profile.Extensions = ["wav"];
        vm.SelectedProfile = images;
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(vm.Palette, c => c.PresetId == "test-jpg");
        vm.Reset(images);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Palette.Single(c => c.PresetId == "test-jpg").Reason);
    }

    [AvaloniaFact]
    public void Closing_the_page_stops_its_judging()
    {
        using var f = new Fixture();
        var first = f.PageWithPalette("png");
        Assert.Equal(0, f.Built.StatusCalls);
        first.Dispose();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, f.Built.StatusCalls);

        using var second = f.PageWithPalette("png");
        Dispatcher.UIThread.RunJobs();
        f.Built.StatusCalls = 0;
        f.Catalog.Refresh();
        second.Dispose();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, f.Built.StatusCalls);
    }

    [AvaloniaFact]
    public void Choosing_a_donut_returns_before_judging_and_the_dispatcher_finishes_it()
    {
        using var f = new Fixture();
        using var vm = f.PageWithPalette("png");
        Assert.Equal(0, f.Built.StatusCalls);
        Assert.Contains(vm.Palette, c => c.PresetId == "test-group");
        Assert.DoesNotContain(vm.Palette, c => c.PresetId is "test-jpg" or "test-mp4");
        Dispatcher.UIThread.RunJobs();
        Assert.True(f.Built.StatusCalls >= 1);
        Assert.Null(vm.Palette.Single(c => c.PresetId == "test-jpg").Reason);
        Assert.True(vm.Palette.Single(c => c.PresetId == "test-mp4").IsDimmed);
    }
}
