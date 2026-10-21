// Theme: light/dark, accent colour, softness (corner radius), the donut's opacity, reduce animations. Changes apply live.

using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;

namespace Filee.App.ViewModels.Pages;

public sealed partial class ThemePageViewModel : ObservableObject
{
    private readonly UserDataStore _store;
    private readonly bool _loading;

    public ThemePageViewModel(UserDataStore store, ILocalizer loc, IPlatformServices platform)
    {
        _store = store;
        _loading = true;
        var theme = store.Settings.Theme;
        Modes = Enum.GetValues<ThemeMode>().Select(m => new Choice<ThemeMode>(m, loc[$"theme.mode.{m}"])).ToList();
        Mode = Modes.First(m => m.Value == theme.Mode);
        Accent = Color.TryParse(theme.Accent, out var c) ? c : Colors.MediumPurple;
        AccentHex = $"#{Accent.R:X2}{Accent.G:X2}{Accent.B:X2}";
        CornerRadius = theme.CornerRadius;
        DonutOpacity = Math.Round(Math.Clamp(store.Settings.Donut.Opacity, DonutSettings.MinOpacity, 1) * 100);
        ReduceMotion = theme.ReduceMotion ?? platform.PrefersReducedMotion;
        PreviewItems =
        [
            new DonutItem { Id = "a", Label = "PNG" },
            new DonutItem { Id = "b", Label = "PDF" },
            new DonutItem { Id = "c", Label = "WEBP" },
            new DonutItem { Id = "d", Label = "HWPX" },
            new DonutItem { Id = "e", Label = "JPG 70%", Caption = "JPG" },
            new DonutItem { Id = "f", Label = "TIFF" },
        ];
        _loading = false;
    }

    public IReadOnlyList<Choice<ThemeMode>> Modes { get; }
    public IReadOnlyList<Color> Swatches { get; } = ThemeService.Swatches.Select(Color.Parse).ToList();
    public IReadOnlyList<DonutItem> PreviewItems { get; }

    [ObservableProperty] private Choice<ThemeMode> _mode = null!;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AccentBrush), nameof(PreviewBackdrop))] private Color _accent;
    [ObservableProperty] private string _accentHex = "";

    public IBrush AccentBrush => new SolidColorBrush(Accent);

    /// <summary>
    /// What the preview donut floats on, standing in for the desktop: a gradient of the accent, so the slices'
    /// opacity shows (on the plain card, slices of its own colour look the same at any opacity).
    /// </summary>
    public IBrush PreviewBackdrop => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(ThemeService.Shade(Accent, 0.55), 0),
            new GradientStop(Accent, 0.5),
            new GradientStop(ThemeService.Shade(Accent, -0.45), 1),
        },
    };
    [ObservableProperty] private double _cornerRadius;
    [ObservableProperty] private bool _reduceMotion;

    /// <summary>
    /// The donut toolbar's opacity in percent: the one setting Donut toolbar → Appearance → Opacity edits too (each
    /// page reads it when it opens). The preview donut follows it at once, the toolbar the next time it opens.
    /// </summary>
    [ObservableProperty] private double _donutOpacity;

    partial void OnModeChanged(Choice<ThemeMode>? oldValue, Choice<ThemeMode> newValue)
    {
        if (!Choices.Restore(oldValue, newValue, v => Mode = v))
            Save(t => t.Mode = newValue.Value);
    }
    partial void OnAccentChanged(Color value)
    {
        var hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
        if (!string.Equals(AccentHex, hex, StringComparison.OrdinalIgnoreCase))
            AccentHex = hex;
        Save(t => t.Accent = hex);
    }

    // Custom colour typed as #RRGGBB; ignored until it parses.
    partial void OnAccentHexChanged(string value)
    {
        if (Color.TryParse(value.Trim(), out var color) && color != Accent)
            Accent = Color.FromRgb(color.R, color.G, color.B);
    }
    partial void OnCornerRadiusChanged(double value) => Save(t => t.CornerRadius = Math.Round(value));
    partial void OnReduceMotionChanged(bool value) => Save(t => t.ReduceMotion = value);

    partial void OnDonutOpacityChanged(double value)
    {
        if (_loading)
            return;
        _store.Settings.Donut.Opacity = Math.Round(value) / 100;
        _store.SaveSettings();
    }

    private void Save(Action<ThemeSettings> change)
    {
        if (_loading)
            return;
        change(_store.Settings.Theme);
        _store.SaveSettings(); // App re-applies the theme on SettingsChanged
    }
}
