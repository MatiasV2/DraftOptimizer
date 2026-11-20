using DraftOptimizer.Core;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DraftOptimizer;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer _sharedDatasetTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private string? _sharedDatasetVersion;
    private bool _sharedDatasetPolling;

    private void InitializeSharedDatasetReader()
    {
        _sharedDatasetTimer.Tick += async (_, _) =>
        {
            if (_firebase.CanPublishDataset || _busy || _loadingData || _sharedDatasetPolling) return;
            try { await ApplySharedDatasetAsync(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { DataText.Text = "Dataset compartido pendiente · " + ex.Message; }
        };
        Closed += (_, _) => _sharedDatasetTimer.Stop();
        _sharedDatasetTimer.Start();
    }

    private async Task ApplySharedDatasetAsync()
    {
        if (_sharedDatasetPolling) return;
        _sharedDatasetPolling = true;
        try
        {
            DataText.Text = "Consultando dataset compartido de Firestore…";
            var shared = await _firebase.LoadSharedDatasetAsync(
                Path.Combine(_directory, "cache", "shared-diamond-7-days.json"), _closed.Token);
            if (_firebase.CanPublishDataset) return;
            if (shared.Version != _sharedDatasetVersion || _context == null)
            {
                var data = new Dataset(shared.Payload);
                var context = new Dataset(shared.Payload);
                BindCatalog(data);
                _context?.Dispose();
                _context = context;
                _sharedDatasetVersion = shared.Version;
                _lastSuggestions = null;
                RefreshSuggestions();
                RefreshSimulation();
            }
            DataText.Text = $"Diamante+ · últimos 7 días · {_data?.Date} · {(shared.Cached ? "caché compartida" : "Firestore")}";
            if (_profilesValid && _favoritesValid) Notice.Visibility = System.Windows.Visibility.Collapsed;
        }
        finally { _sharedDatasetPolling = false; }
    }
}
