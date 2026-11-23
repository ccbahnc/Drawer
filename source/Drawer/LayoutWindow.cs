using System;
using System.Collections.Generic;
using System.Numerics;
using Drawer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;

namespace Drawer;

internal enum LayoutEdit { Preview, Restore, Commit }

internal sealed class LayoutSpaceException(Exception? inner = null) : ArgumentException(AutomaticLayout.Unavailable, inner);

internal sealed class LayoutWindow : Window
{
    private readonly LayoutSelection selection;
    private readonly Border[,] cells = new Border[LayoutSelection.GridRows, LayoutSelection.GridColumns];
    private readonly TextBox rows = new() { Width = 102, MinWidth = 0, Height = 32, Padding = new Thickness(6, 3, 6, 3) };
    private readonly TextBox columns = new() { Width = 102, MinWidth = 0, Height = 32, Padding = new Thickness(6, 3, 6, 3) };
    private readonly TextBlock rowsLabel = new() { FontSize = 12 };
    private readonly TextBlock columnsLabel = new() { FontSize = 12 };
    private readonly List<(RepeatButton Button, bool Row, int Delta)> arrows = new();
    private readonly StackPanel body;
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Width = 264, Height = 38,
        FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Func<string> label;
    private readonly Action<int, int, LayoutEdit> change;
    private readonly Func<NativeMethods.Point, bool> isTrayIconPoint;
    private TextBox? composing, imeEnter, edited;
    private bool closing, preview, imeEscape, paintingEditors;
    private bool baseAvailable = true;
    private Exception? warning;
    internal bool MenuOpen { get; set; }
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _outsideTimer;
    internal bool OutsideTimerRunning => _outsideTimer?.IsRunning == true;

    public LayoutWindow(Func<string> label, int initialRows, int initialColumns, Action<int, int, LayoutEdit> change,
        double scale, Func<NativeMethods.Point, bool> isTrayIconPoint)
    {
        this.label = label;
        this.change = change;
        this.isTrayIconPoint = isTrayIconPoint;
        selection = new(initialRows, initialColumns);

        body = new StackPanel { RequestedTheme = DrawerPalette.Theme, Spacing = 7, Padding = new Thickness(10), Background = DrawerPalette.Brush("Menu") };
        error.Foreground = DrawerPalette.Brush("Danger");
        var numbers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        numbers.Children.Add(Editor(rows, true));
        numbers.Children.Add(Editor(columns, false));
        body.Children.Add(numbers);

        var grid = new Grid { Width = 264, Height = 176, HorizontalAlignment = HorizontalAlignment.Left, Background = DrawerPalette.Brush("Transparent") };
        for (int row = 0; row < LayoutSelection.GridRows; row++) grid.RowDefinitions.Add(new());
        for (int col = 0; col < LayoutSelection.GridColumns; col++) grid.ColumnDefinitions.Add(new());
        for (int r = 0; r < LayoutSelection.GridRows; r++)
        for (int c = 0; c < LayoutSelection.GridColumns; c++)
        {
            int row = r + 1, col = c + 1;
            var cell = new Border { Margin = new Thickness(1.5), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2), BorderBrush = DrawerPalette.Brush("SelectionBorder") };
            cells[r, c] = cell;
            Grid.SetRow(cell, r);
            Grid.SetColumn(cell, c);
            grid.Children.Add(cell);
            cell.PointerEntered += (_, _) => Preview(row, col);
            cell.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(cell).Properties.IsLeftButtonPressed || composing != null) return;
                if (edited != null)
                {
                    // This click selects a grid candidate. Do not also commit a text edit on focus loss.
                    edited = null;
                    PaintEditors();
                }
                if (Commit(row, col)) CloseLayout();
                e.Handled = true;
            };
        }
        grid.PointerExited += (_, e) =>
        {
            var point = e.GetCurrentPoint(grid).Position;
            if (point.X < 0 || point.Y < 0 || point.X >= grid.ActualWidth || point.Y >= grid.ActualHeight) CancelPreview();
        };
        body.Children.Add(grid);
        body.Children.Add(error);
        body.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Escape || composing != null || imeEscape) return;
            CloseLayout();
            e.Handled = true;
        };
        body.KeyUp += (_, e) => { if (e.Key == VirtualKey.Escape) imeEscape = false; };
        Content = new ScrollViewer { Content = body,
            HorizontalScrollMode = ScrollMode.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        ApplyStyle();
        ApplyLanguage();
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(315 * scale), (int)(320 * scale)));
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        var outside = _outsideTimer = DispatcherQueue.CreateTimer();
        outside.Interval = TimeSpan.FromMilliseconds(DrawerSettings.PollMs);
        bool leftWasDown = true;
        outside.Tick += (_, _) =>
        {
            using var sample = IdlePerformance.Measure(IdlePerformance.Work.LayoutTimer);
            bool down = (NativeMethods.GetAsyncKeyState(1) & 0x8000) != 0;
            if (!MenuOpen && down && !leftWasDown && NativeMethods.GetCursorPos(out var point) &&
                NativeMethods.GetWindowRect(WinRT.Interop.WindowNative.GetWindowHandle(this), out var bounds) &&
                !bounds.Pixels.Contains(point.X, point.Y) && !isTrayIconPoint(point)) CloseLayout();
            leftWasDown = down;
        };
        Activated += (_, _) => { if (!outside.IsRunning) outside.Start(); };
        Closed += (_, _) =>
        {
            closing = true;
            outside.Stop();
            _outsideTimer = null;
        };
        PaintEditors();
        Paint();
    }

    internal void ShowInitialPreview()
    {
        Preview(selection.Rows, selection.Columns);
        baseAvailable = preview;
        preview = false;
        Paint();
        PaintWarning();
    }

    internal void ApplyStyle()
    {
        body.RequestedTheme = DrawerPalette.Theme;
        DrawerPalette.ApplyFrame(this, titled: true);
    }

    internal void ApplyLanguage()
    {
        Title = UiText.Get("LayoutWindowTitle", label());
        rowsLabel.Text = UiText.Get("Rows");
        columnsLabel.Text = UiText.Get("Columns");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(rows, rowsLabel.Text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(columns, columnsLabel.Text);
        foreach (var (button, row, delta) in arrows)
        {
            string name = UiText.Get(delta > 0 ? "Increase" : "Decrease", row ? rowsLabel.Text : columnsLabel.Text);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
            ToolTipService.SetToolTip(button, name);
        }
        for (int r = 0; r < LayoutSelection.GridRows; r++)
        for (int c = 0; c < LayoutSelection.GridColumns; c++)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cells[r, c], UiText.Get("LayoutCellName", r + 1, c + 1));
        PaintWarning();
    }

    internal void RestoreAfterMenu()
    {
        Activate();
        edited?.Focus(FocusState.Programmatic);
    }

    private void CloseLayout()
    {
        if (closing) return;
        closing = true;
        Close();
    }

    private FrameworkElement Editor(TextBox box, bool row)
    {
        box.GotFocus += (_, _) => CancelPreview();
        box.BeforeTextChanging += (_, _) => { if (!paintingEditors && !closing) edited = box; };
        box.TextCompositionStarted += (_, _) => composing = box;
        box.TextCompositionEnded += (_, _) =>
        {
            composing = null;
            if ((NativeMethods.GetAsyncKeyState(0x0D) & 0x8000) != 0) imeEnter = box;
            if ((NativeMethods.GetAsyncKeyState(0x1B) & 0x8000) != 0) imeEscape = true;
            if (!MenuOpen && box.FocusState == FocusState.Unfocused && edited == box)
                DispatcherQueue.TryEnqueue(() => { if (!MenuOpen) CommitEditor(box); });
        };
        box.KeyUp += (_, e) => { if (e.Key == VirtualKey.Enter && imeEnter == box) imeEnter = null; };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter || composing == box || imeEnter == box) return;
            CommitEditor(box);
            e.Handled = true;
        };
        box.LostFocus += (_, _) =>
        {
            if (!MenuOpen && !(NativeMethods.GetCursorPos(out var point) && isTrayIconPoint(point)))
                CommitEditor(box);
        };
        var editor = new StackPanel { Spacing = 3 };
        var caption = row ? rowsLabel : columnsLabel;
        caption.Foreground = DrawerPalette.Brush("Foreground");
        editor.Children.Add(caption);
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(box);
        var steps = new StackPanel { Width = 24 };
        foreach (int delta in new[] { 1, -1 })
        {
            var button = new RepeatButton { Width = 24, Height = 16, MinWidth = 0, MinHeight = 0,
                AllowFocusOnInteraction = false, Padding = new Thickness(0), Delay = 350, Interval = 100,
                Background = DrawerPalette.Brush("Menu"), BorderBrush = DrawerPalette.Brush("Outline"),
                Content = new FontIcon { Glyph = delta > 0 ? "\uE70E" : "\uE70D", FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 8, Foreground = DrawerPalette.Brush("Foreground") } };
            button.Click += (_, _) =>
            {
                if (composing != null) return;
                if (edited is { } editorBox) CommitEditor(editorBox);
                int r = selection.Rows + (row ? delta : 0);
                int c = selection.Columns + (row ? 0 : delta);
                if (LayoutSelection.Valid(r, c)) Commit(r, c);
            };
            arrows.Add((button, row, delta));
            steps.Children.Add(button);
        }
        line.Children.Add(steps);
        editor.Children.Add(line);
        return editor;
    }

    private void CommitEditor(TextBox box)
    {
        if (closing || composing == box || edited != box) return;
        edited = null;
        bool isRow = box == rows;
        if (!BigInteger.TryParse(box.Text, out var value))
        {
            PaintEditors();
            return;
        }
        int next = (int)BigInteger.Min(LayoutSelection.Maximum, BigInteger.Max(LayoutSelection.Minimum, value));
        Commit(isRow ? next : selection.ConfirmedRows, isRow ? selection.ConfirmedColumns : next);
    }

    private void Preview(int row, int col)
    {
        if (closing || composing != null || edited != null) return;
        try
        {
            change(row, col, LayoutEdit.Preview);
            selection.Preview(row, col);
            preview = true;
            PaintEditors();
            Paint();
            ClearError();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void CancelPreview()
    {
        if (closing) return;
        try
        {
            if (preview) change(selection.ConfirmedRows, selection.ConfirmedColumns, LayoutEdit.Restore);
            preview = false;
            selection.Cancel();
            PaintEditors();
            Paint();
            if (baseAvailable) ClearError();
            else ShowError(new LayoutSpaceException());
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private bool Commit(int row, int col)
    {
        try
        {
            change(row, col, LayoutEdit.Commit);
            selection.Confirm(row, col);
            baseAvailable = true;
            preview = false;
            PaintEditors();
            Paint();
            ClearError();
            return true;
        }
        catch (Exception ex)
        {
            PaintEditors();
            ShowError(ex);
            return false;
        }
    }

    private void PaintEditors()
    {
        paintingEditors = true;
        try
        {
            if (edited != rows && composing != rows && rows.Text != selection.Rows.ToString()) rows.Text = selection.Rows.ToString();
            if (edited != columns && composing != columns && columns.Text != selection.Columns.ToString()) columns.Text = selection.Columns.ToString();
        }
        finally { paintingEditors = false; }
    }

    private void Paint()
    {
        foreach (var (button, row, delta) in arrows)
        {
            int value = row ? selection.Rows : selection.Columns;
            button.IsEnabled = delta > 0 ? value < LayoutSelection.Maximum : value > LayoutSelection.Minimum;
        }
        for (int r = 0; r < LayoutSelection.GridRows; r++)
        for (int c = 0; c < LayoutSelection.GridColumns; c++)
            cells[r, c].Background = DrawerPalette.Brush((baseAvailable || preview) &&
                r < selection.Rows && c < selection.Columns ? "LayoutPreview" : "LayoutCell");
    }

    private void ShowError(Exception ex) { warning = ex; PaintWarning(); }
    private void ClearError() { warning = null; PaintWarning(); }
    private void PaintWarning() => error.Text = warning is LayoutSpaceException
        ? UiText.Get(baseAvailable || preview ? "LayoutRetained" : "LayoutUnavailable")
        : warning?.Message ?? "";
}
