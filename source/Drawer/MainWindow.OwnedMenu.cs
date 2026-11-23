using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Drawer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using static Drawer.NativeMethods;

namespace Drawer;
public sealed partial class MainWindow
{
    private const int ToggleNamesCommand = 3;
    private const int FirstCreateCommand = 10;
    private const int LightCommand = 50, DarkCommand = 51, SolidCommand = 52, AcrylicCommand = 53, BlurCommand = 54;
    private const int ChineseCommand = 55, EnglishCommand = 56;
    private const int TransparentCommand = 57;
    private const int UndoCommand = 60, RedoCommand = 61;
    private const int ToggleStartupCommand = 62, RemoveStartupCommand = 63;
    private const int ToggleNavigationCommand = 64;
    private const int ToggleFullScreenCommand = 65;
    private MenuFlyout? _ownedMenu;
    private TaskCompletionSource<int>? _menuCompletion;

    private sealed record MenuRow(string? Title, int Command = 0, bool Enabled = true, bool? Checked = null,
        bool Danger = false, DrawerMenu? Submenu = null, string? Tooltip = null);

    private sealed class DrawerMenu
    {
        public readonly List<MenuRow> Rows = new();
        public void Add(int command, string title, bool enabled = true, bool? isChecked = null, bool danger = false, string? tooltip = null) =>
            Rows.Add(new(title, command, enabled, isChecked, danger, Tooltip: tooltip));
        public void Separate() => Rows.Add(new(null));
        public void AddSubmenu(string title, DrawerMenu submenu, bool danger = false) => Rows.Add(new(title, Danger: danger, Submenu: submenu));
    }

    private void AppendSharedDrawerMenu(DrawerMenu menu)
    {
        AppendAddDrawerMenu(menu);
        menu.Add(ToggleNamesCommand, UiText.Get("ShowIconNames"), isChecked: _preferences.Value.Global.ShowNames);
        menu.Add(ToggleFullScreenCommand, UiText.Get("AllowInFullScreen"), isChecked: _preferences.Value.Global.AllowInFullScreen,
            tooltip: UiText.Get("AllowInFullScreenTooltip"));
        AppendLanguageMenu(menu);
        AppendAppearanceMenus(menu);
    }

    private static void AppendAddDrawerMenu(DrawerMenu menu)
    {
        var add = new DrawerMenu();
        foreach (var edge in Enum.GetValues<DrawerEdge>())
            add.Add(FirstCreateCommand + (int)edge, DrawerCatalog.Label(edge));
        menu.AddSubmenu(UiText.Get("AddDrawer"), add);
    }

    private static void AppendLanguageMenu(DrawerMenu menu)
    {
        var languages = new DrawerMenu();
        languages.Add(ChineseCommand, UiText.Get("ChineseChoice"), isChecked: UiText.Language == DrawerLanguage.SimplifiedChinese);
        languages.Add(EnglishCommand, UiText.Get("EnglishChoice"), isChecked: UiText.Language == DrawerLanguage.English);
        menu.AddSubmenu(UiText.Get("Language"), languages);
    }

    private void AppendAppearanceMenus(DrawerMenu menu)
    {
        var appearance = _preferences.Value.Global;
        var colors = new DrawerMenu();
        colors.Add(LightCommand, UiText.Get("Light"), isChecked: appearance.ColorScheme == DrawerColorScheme.Light);
        colors.Add(DarkCommand, UiText.Get("Dark"), isChecked: appearance.ColorScheme == DrawerColorScheme.Dark);
        menu.AddSubmenu(UiText.Get("ColorScheme"), colors);
        var styles = new DrawerMenu();
        styles.Add(SolidCommand, UiText.Get("Solid"), isChecked: appearance.Material == DrawerMaterial.Solid);
        styles.Add(TransparentCommand, UiText.Get("Transparent"), isChecked: appearance.Material == DrawerMaterial.Transparent);
        styles.Add(BlurCommand, UiText.Get("Blur"), isChecked: appearance.Material == DrawerMaterial.Blur);
        styles.Add(AcrylicCommand, UiText.Get("Acrylic"), isChecked: appearance.Material == DrawerMaterial.Acrylic);
        menu.AddSubmenu(UiText.Get("Material"), styles);
    }

    private bool RunSharedDrawerCommand(int command)
    {
        if (_exitRequested) return true;
        if (command == ToggleFullScreenCommand)
        {
            // Commit first: a failed save must not change runtime behavior or menu state.
            _preferences.SetAllowInFullScreen(!_preferences.Value.Global.AllowInFullScreen);
            _gate.Invalidate();
            foreach (var preview in _previewSurfaces.Values) preview._gate.Invalidate();
            IdlePerformance.Invalidate(IdlePerformance.Change.Configuration | IdlePerformance.Change.Scene);
            _wasApproachingEdge = false;
            SetIdlePolling(false);
            PrototypeLog.Write($"fullscreen reveal: enabled={_preferences.Value.Global.AllowInFullScreen}");
            return true;
        }
        if (command == ToggleNavigationCommand)
        {
            ToggleNavigation();
            return true;
        }
        if (command is ChineseCommand or EnglishCommand)
        {
            _preferences.SetLanguage(command == ChineseCommand ? DrawerLanguage.SimplifiedChinese : DrawerLanguage.English);
            ApplyUiLanguage();
            return true;
        }
        if (command is LightCommand or DarkCommand or SolidCommand or AcrylicCommand or BlurCommand or TransparentCommand)
        {
            if (command is LightCommand or DarkCommand)
                _preferences.SetColorScheme(command == DarkCommand ? DrawerColorScheme.Dark : DrawerColorScheme.Light);
            else _preferences.SetMaterial(command switch
            {
                TransparentCommand => DrawerMaterial.Transparent,
                BlurCommand => DrawerMaterial.Blur,
                AcrylicCommand => DrawerMaterial.Acrylic,
                _ => DrawerMaterial.Solid
            });
            ApplyAppearance();
            var appearance = _preferences.Value.Global;
            PrototypeLog.Write($"appearance: {appearance.ColorScheme}/{appearance.Material}");
            return true;
        }
        if (command == ToggleNamesCommand)
        {
            _preferences.SetShowNames(!_preferences.Value.Global.ShowNames);
            ApplyPreferences();
            return true;
        }
        if (command is < FirstCreateCommand or > FirstCreateCommand + (int)DrawerEdge.Right) return false;
        BeginCreate((DrawerEdge)(command - FirstCreateCommand));
        return true;
    }

    private void ToggleNavigation()
    {
        if (_layoutWindow != null || _confirmationWindow != null || NavigationTransitionActive ||
            _displayRefreshPending || !FinishNameEdit(true)) return;
        ObserveDisplay();
        if (_displayRefreshPending) return;
        var saved = _preferences.Value;
        var proposal = saved with { Global = saved.Global with { ShowNavigation = !saved.Global.ShowNavigation } };
        var plan = ComputeLayout(proposal);
        if (proposal.Global.ShowNavigation)
        {
            var before = ComputeLayout(saved);
            if (saved.Drawers.Any(d => d.Enabled && before.Items.ContainsKey(d.Id) && !plan.Items.ContainsKey(d.Id)))
            {
                var message = UiText.Get("NavigationSpaceUnavailable");
                if (_visible) ShowOperationFeedback(message);
                else ErrorStatus(message);
                return;
            }
        }
        _switchDwell.Clear();
        _switchTarget = null;
        _switching = false;
        _itemDrawerDwell.Clear();
        _pageEdgeDwell.Reset();
        _pageWheel.Reset();
        if (!_visible) _interaction.Reset();
        ClearOperationFeedback();
        _preferences.SetShowNavigation(proposal.Global.ShowNavigation);
        _resizeScales.Clear();
        if (_visible) BeginNavigationTransition(saved.Global.ShowNavigation);
        UpdateGeometry(immediateSize: !_visible, finalPlan: plan);
        if (_visible) RenderPosition(preserveSurface: true);
    }

    private async Task<int> ShowOwnedMenu(DrawerMenu menu, Point anchor, bool fromTray = false)
    {
        if (_closed || _menuOpen || menu.Rows.Count == 0) return 0;
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var flyout = new MenuFlyout
        {
            ShouldConstrainToRootBounds = false,
            MenuFlyoutPresenterStyle = (Style)Application.Current.Resources["DrawerMenuPresenterStyle"]
        };
        int command = 0;
        PopulateMenu(flyout.Items, menu, selected => command = selected);
        flyout.Opened += (_, _) => PrototypeLog.Write($"menu: opened tray={fromTray} foreground={GetForegroundWindow()}");
        flyout.Closed += (_, _) =>
        {
            PrototypeLog.Write($"menu: closed tray={fromTray} command={command} foreground={GetForegroundWindow()}");
            completion.TrySetResult(command);
        };
        _ownedMenu = flyout;
        _menuCompletion = completion;
        _menuOpen = true;
        using var hold = HoldOpen();
        MenuAnchorWindow? trayAnchor = null;
        try
        {
            FrameworkElement target = WindowSurface;
            if (fromTray)
            {
                trayAnchor = new MenuAnchorWindow();
                target = await trayAnchor.Open(anchor);
            }
            else
            {
                if (!ScreenToClient(_hwnd, ref anchor)) return 0;
                ActivateContentInput();
            }
            if (_closed || completion.Task.IsCompleted || target.XamlRoot == null) return 0;
            double scale = target.XamlRoot.RasterizationScale;
            flyout.ShowAt(target, new FlyoutShowOptions
            {
                Position = fromTray ? new Windows.Foundation.Point() : new(anchor.X / scale, anchor.Y / scale),
                Placement = fromTray ? FlyoutPlacementMode.TopEdgeAlignedLeft :
                    _edge == DrawerEdge.Right ? FlyoutPlacementMode.LeftEdgeAlignedTop : FlyoutPlacementMode.Auto,
                ShowMode = FlyoutShowMode.Standard
            });
            // Opening is asynchronous. IsOpen can still be false immediately after ShowAt.
            // Keep the anchor and operation alive until WinUI reports Closed or we cancel.
            trayAnchor?.ObserveDismissal(() =>
            {
                if (ReferenceEquals(_ownedMenu, flyout) && command == 0) DismissOwnedMenu();
            }, IsTrayIconPoint);
            return await completion.Task;
        }
        finally
        {
            _ownedMenu = null;
            _menuCompletion = null;
            _menuOpen = false;
            trayAnchor?.Close();
            KeepEscapeOnClosedSurface();
        }
    }

    private void DismissOwnedMenu()
    {
        _menuCompletion?.TrySetResult(0);
        if (_ownedMenu is { IsOpen: true } menu) menu.Hide();
    }

    private static void PopulateMenu(IList<MenuFlyoutItemBase> items, DrawerMenu menu, Action<int> select)
    {
        foreach (var row in menu.Rows)
        {
            if (row.Title == null) { items.Add(new MenuFlyoutSeparator()); continue; }
            var danger = row.Danger ? DrawerPalette.Brush("Danger") : null;
            if (row.Submenu != null)
            {
                var submenu = new MenuFlyoutSubItem { Text = row.Title };
                if (danger != null)
                {
                    submenu.Foreground = danger;
                    submenu.Icon = new FontIcon { Glyph = "\uE74D", Foreground = danger };
                }
                PopulateMenu(submenu.Items, row.Submenu, select);
                items.Add(submenu);
                continue;
            }
            MenuFlyoutItem item = row.Checked is bool isChecked ? new ToggleMenuFlyoutItem { IsChecked = isChecked } : new MenuFlyoutItem();
            item.Text = row.Title;
            if (row.Tooltip != null) ToolTipService.SetToolTip(item, row.Tooltip);
            item.IsEnabled = row.Enabled;
            if (danger != null)
            {
                item.Foreground = danger;
                item.Icon = new FontIcon { Glyph = "\uE74D", Foreground = danger };
                item.Resources["MenuFlyoutItemForegroundPointerOver"] = danger;
                item.Resources["MenuFlyoutItemForegroundPressed"] = danger;
            }
            item.Click += (_, _) => select(row.Command);
            items.Add(item);
        }
    }

    private Point ScreenAnchor(Microsoft.UI.Xaml.UIElement element, double x, double y)
    {
        var point = element.TransformToVisual((Microsoft.UI.Xaml.UIElement)Content)
            .TransformPoint(new Windows.Foundation.Point(x, y));
        var anchor = new Point { X = (int)Math.Round(point.X * _presentationScale), Y = (int)Math.Round(point.Y * _presentationScale) };
        ClientToScreen(_hwnd, ref anchor);
        return anchor;
    }
}
