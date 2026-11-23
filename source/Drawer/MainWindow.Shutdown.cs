using System;
using System.Threading.Tasks;
using Drawer.Core;

namespace Drawer;

public sealed partial class MainWindow
{
    private bool _exitRequested, _allowExit, _exitCommitFailed;

    private async void ExitDrawer()
    {
        if (_closed || _exitRequested) return;
        _exitRequested = true;
        using var hold = HoldOpen();
        RootGrid.IsHitTestVisible = false;
        _pendingHistory.Clear();
        try
        {
            DismissOwnedMenu();
            _layoutWindow?.Close();
            _confirmationWindow?.Close();
            _importCancellation?.Cancel();
            var waiting = System.Diagnostics.Stopwatch.StartNew();
            // Data workers finish their atomic commit or roll back before the HWND closes.
            while (_importing || _readingClipboard || _historyRestoring || _committingCut || _committingDrag ||
                _formingFolder || _morphCommitted)
            {
                if (waiting.Elapsed > TimeSpan.FromSeconds(30))
                    throw new InvalidOperationException(UiText.Get("ExitBusy"));
                await Task.Delay(50);
                if (_closed) return;
            }
            if (_exitCommitFailed) throw new InvalidOperationException(UiText.Get("ExitCommitFailed"));
            if (!FinishNameEdit(true)) throw new InvalidOperationException(UiText.Get("FinishEditingBeforeExit"));
            _allowExit = true;
            FinishExitDrawer();
        }
        catch (Exception ex)
        {
            _allowExit = false;
            _exitRequested = false;
            _exitCommitFailed = false;
            if (!_closed)
            {
                RootGrid.IsHitTestVisible = true;
                ErrorStatus(UiText.Get("ExitDeferred", ex.Message));
            }
        }
    }
}
