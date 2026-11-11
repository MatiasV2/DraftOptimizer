using DraftOptimizer.Core;
using Microsoft.UI.Xaml;
using System;
using System.Threading;

namespace DraftOptimizer;
public sealed partial class MainWindow
{
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private CancellationTokenSource? _suggestionsCancellation;
    private long _suggestionsVersion;
    private Dataset? _lastSuggestionsData, _lastSuggestionsContext;
    private string? _capturedDraftKey;
    private DraftSession? _capturedDraft;
    private DraftSession? CaptureDraftSnapshot(out string key)
    {
        key = _draft == null ? "" : Json.Write(new { _draft.GameId, _draft.QueueId, _draft.BenchEnabled, _draft.AllowRerolling, _draft.BenchChampionIds, _draft.BenchChampions, _draft.LocalPlayerCellId, _draft.MyTeam, _draft.TheirTeam, _draft.Actions, Timer = _draft.Timer == null ? null : new { _draft.Timer.Phase } });
        if (key != _capturedDraftKey) { _capturedDraftKey = key; _capturedDraft = key.Length == 0 ? null : Json.Read<DraftSession>(key); }
        return _capturedDraft;
    }
    private void InitializeBackgroundWork()
    {
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); RefreshSuggestions(); };
        Closed += (_, _) =>
        {
            _searchDelay.Stop(); _suggestionsCancellation?.Cancel(); _simulationCancellation?.Cancel();
            ((PortraitImageConverter)Root.Resources["PortraitImageConverter"]).Dispose();
        };
    }
}


