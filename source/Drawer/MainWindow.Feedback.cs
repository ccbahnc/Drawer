using System;
using System.Collections.Generic;
using Drawer.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Drawer;

public sealed partial class MainWindow
{
    private Border? _operationFeedback;
    private DispatcherQueueTimer? _feedbackTimer;
    private bool ShowingOperationFeedback => _operationFeedback != null;

    private void ShowOperationFeedback(string message)
    {
        Status(message);
        if (_closed || !_visible || WindowSurface.XamlRoot == null) return;
        ClearOperationFeedback();
        double surfaceWidth = double.IsFinite(DropSurface.Width) ? DropSurface.Width : WindowSurface.ActualWidth;

        var text = new TextBlock
        {
            Text = message, MaxLines = 4, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap,
            Foreground = DrawerPalette.Brush("Foreground"), FontSize = 12
        };
        var card = new Border
        {
            Child = text, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(7),
            Background = DrawerPalette.Brush("Menu"), BorderBrush = DrawerPalette.Brush("Outline"),
            BorderThickness = new Thickness(1), IsHitTestVisible = false,
            MaxWidth = Math.Max(1, Math.Min(300, surfaceWidth - 12)),
            Margin = new Thickness(6, 6, 6, 0),
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center
        };
        try
        {
            // Append after DropSurface so the notice stays above the folder and icon layers.
            _operationFeedback = card;
            WindowSurface.Children.Add(card);
            _feedbackTimer ??= DispatcherQueue.CreateTimer();
            _feedbackTimer.IsRepeating = false;
            _feedbackTimer.Interval = TimeSpan.FromSeconds(4);
            _feedbackTimer.Tick -= FeedbackExpired;
            _feedbackTimer.Tick += FeedbackExpired;
            _feedbackTimer.Start();
        }
        catch (Exception ex)
        {
            ClearOperationFeedback();
            PrototypeLog.Write("drawer feedback: " + ex.Message);
        }
    }

    private void FeedbackExpired(DispatcherQueueTimer sender, object args)
    {
        using var sample = IdlePerformance.Measure(IdlePerformance.Work.FeedbackTimer);
        ClearOperationFeedback();
    }

    private void ClearOperationFeedback()
    {
        _feedbackTimer?.Stop();
        if (_operationFeedback == null) return;
        WindowSurface.Children.Remove(_operationFeedback);
        _operationFeedback = null;
    }

    private void ReportTransferResult(bool move, int succeeded, int duplicates, int failed, string? reason = null)
    {
        if (duplicates == 0 && failed == 0) return;
        if (!move && succeeded == 0 && failed == 0)
        {
            ShowOperationFeedback(UiText.Get("ImportAllDuplicates"));
            return;
        }
        var parts = new List<string>();
        if (succeeded > 0) parts.Add(UiText.Get(move ? "ItemsMoved" : "ItemsAdded", succeeded));
        if (duplicates > 0) parts.Add(UiText.Get("ItemsSkipped", duplicates));
        if (failed > 0)
        {
            string key = succeeded > 0 || duplicates > 0 ? "ItemsFailed"
                : move ? "MoveNoneSucceeded" : "ImportNoneSucceeded";
            parts.Add(UiText.Get(key, failed));
        }
        if (!string.IsNullOrEmpty(reason)) parts.Add(reason);
        ShowOperationFeedback(string.Join(" ", parts));
    }
}
