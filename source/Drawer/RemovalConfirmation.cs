using System;
using System.Threading.Tasks;
using Drawer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using static Drawer.NativeMethods;

namespace Drawer;

internal sealed class RemovalConfirmation : Window
{
    private readonly Grid body;
    private readonly TextBlock heading;
    private readonly TextBlock description;
    private readonly Button cancel;
    private readonly Button apply;
    private readonly int items, folders;
    private readonly bool combine;
    private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool confirmed;

    public RemovalConfirmation(nint owner, int items, int folders, PixelRect work, double scale, bool combine)
    {
        this.items = items;
        this.folders = folders;
        this.combine = combine;
        body = new Grid { Padding = new Thickness(20), Background = DrawerPalette.Brush("Menu"), RowSpacing = 12 };
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        heading = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = DrawerPalette.Brush("Foreground")
        };
        description = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = DrawerPalette.Brush("Secondary")
        };
        var text = new StackPanel { Spacing = 10 };
        text.Children.Add(heading);
        text.Children.Add(description);
        body.Children.Add(new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        cancel = new Button { MinWidth = 80, Background = DrawerPalette.Brush("Input"), Foreground = DrawerPalette.Brush("Foreground") };
        apply = new Button { MinWidth = 92 };
        if (combine) apply.Style = (Style)Application.Current.Resources["DrawerPrimaryButtonStyle"];
        else
        {
            apply.Background = DrawerPalette.Brush("Input");
            apply.Foreground = DrawerPalette.Brush("Danger");
        }
        cancel.Click += (_, _) => Close();
        apply.Click += (_, _) => { confirmed = true; Close(); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        Grid.SetRow(buttons, 1);
        body.Children.Add(buttons);
        body.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Escape) return;
            e.Handled = true;
            Close();
        }), true);
        body.Loaded += (_, _) => cancel.Focus(FocusState.Programmatic);

        Content = body;
        ApplyStyle();
        ApplyLanguage();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        AppWindow.IsShownInSwitchers = false;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetWindowLongPtrW(hwnd, -8, owner);
        int width = Math.Min(work.Width, (int)Math.Round(470 * scale));
        int height = Math.Min(work.Height, (int)Math.Round(255 * scale));
        GetWindowRect(owner, out var ownerBounds);
        int x = Math.Clamp((ownerBounds.Left + ownerBounds.Right - width) / 2, work.Left, work.Right - width);
        int y = Math.Clamp((ownerBounds.Top + ownerBounds.Bottom - height) / 2, work.Top, work.Bottom - height);
        SetWindowPos(hwnd, (nint)(-1), x, y, width, height, 0x0010);
        Closed += (_, _) => completion.TrySetResult(confirmed);
    }

    public Task<bool> Result => completion.Task;

    internal void ApplyLanguage()
    {
        Title = UiText.Get(combine ? "CreateFolderTitle" : "DeleteSelectionTitle");
        heading.Text = UiText.Get(combine ? "ConfirmFolderMerge" : "ConfirmFolderRemoval");
        description.Text = UiText.Get(combine ? "SelectionCounts" : "RemovalCounts", items, folders);
        cancel.Content = UiText.Get("Cancel");
        apply.Content = UiText.Get(combine ? "Merge" : "ConfirmDelete");
    }

    internal void ApplyStyle()
    {
        body.RequestedTheme = DrawerPalette.Theme;
        DrawerPalette.ApplyFrame(this, titled: true);
    }
}
