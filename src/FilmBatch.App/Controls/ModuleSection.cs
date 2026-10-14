using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace FilmBatch.App.Controls;

/// <summary>
/// 模組區塊（計畫書 2.5）：標題列（▸ 折疊｜☑ 啟用｜名稱｜● 覆寫數｜⋯ 選單）＋ 內容區。
/// 啟用勾選框關閉時，內容區變為停用色但仍可預先調整（:module-disabled，不使用 IsEnabled=false）。
/// 樣板定義於 Themes/Controls.axaml。
/// </summary>
public class ModuleSection : HeaderedContentControl
{
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<ModuleSection, bool>(nameof(IsExpanded), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsModuleEnabledProperty =
        AvaloniaProperty.Register<ModuleSection, bool>(nameof(IsModuleEnabled), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<int> OverrideCountProperty =
        AvaloniaProperty.Register<ModuleSection, int>(nameof(OverrideCount));

    public static readonly DirectProperty<ModuleSection, bool> HasOverridesProperty =
        AvaloniaProperty.RegisterDirect<ModuleSection, bool>(nameof(HasOverrides), o => o.HasOverrides);

    public static readonly StyledProperty<FlyoutBase?> HeaderFlyoutProperty =
        AvaloniaProperty.Register<ModuleSection, FlyoutBase?>(nameof(HeaderFlyout));

    private bool _hasOverrides;

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public bool IsModuleEnabled
    {
        get => GetValue(IsModuleEnabledProperty);
        set => SetValue(IsModuleEnabledProperty, value);
    }

    public int OverrideCount
    {
        get => GetValue(OverrideCountProperty);
        set => SetValue(OverrideCountProperty, value);
    }

    public bool HasOverrides
    {
        get => _hasOverrides;
        private set => SetAndRaise(HasOverridesProperty, ref _hasOverrides, value);
    }

    /// <summary>⋯ 選單（重設此模組、只複製此模組）。</summary>
    public FlyoutBase? HeaderFlyout
    {
        get => GetValue(HeaderFlyoutProperty);
        set => SetValue(HeaderFlyoutProperty, value);
    }

    static ModuleSection()
    {
        IsModuleEnabledProperty.Changed.AddClassHandler<ModuleSection>((s, _) => s.UpdatePseudoClasses());
        OverrideCountProperty.Changed.AddClassHandler<ModuleSection>((s, _) => s.HasOverrides = s.OverrideCount > 0);
    }

    public ModuleSection() => UpdatePseudoClasses();

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        // 點標題文字折疊或展開
        if (e.NameScope.Find<Control>("PART_Title") is { } title)
            title.PointerPressed += (_, args) =>
            {
                if (args.GetCurrentPoint(title).Properties.IsLeftButtonPressed)
                    IsExpanded = !IsExpanded;
            };
    }

    private void UpdatePseudoClasses() => PseudoClasses.Set(":module-disabled", !IsModuleEnabled);
}
