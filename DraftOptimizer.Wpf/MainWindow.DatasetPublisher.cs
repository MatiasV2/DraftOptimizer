using DraftOptimizer.Core;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DraftOptimizer;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer _datasetPublishTimer = new() { Interval = TimeSpan.FromMinutes(10) };
    private bool _datasetPublishing;

    private void InitializeDatasetPublisher()
    {
        _datasetPublishTimer.Tick += async (_, _) => await PublishDailyDatasetAsync();
        Closed += (_, _) => _datasetPublishTimer.Stop();
        _datasetPublishTimer.Start();
    }

    private async Task PublishDailyDatasetAsync()
    {
        if (_datasetPublishing || !_firebase.CanPublishDataset || _closed.IsCancellationRequested) return;
        _datasetPublishing = true;
        try
        {
            var previous = await _firebase.DatasetPublicationAsync(_closed.Token);
            if (previous != null && DateTimeOffset.UtcNow - previous.PublishedAt < TimeSpan.FromHours(24)) return;
            var user = _firebase.UserId;
            var (catalog, _) = await new DatasetProvider(_http, Path.Combine(_directory, "cache")).LoadAsync("current-patch", _closed.Token);
            using (catalog)
            {
                var provider = new DiamondStatisticsProvider(_http, Path.Combine(_directory, "cache"));
                var progress = new Progress<string>(message => CloudSyncStatus.Text = "Publicación diaria · " + message);
                var (dataset, cached) = await provider.LoadAsync(catalog, "7-days", _closed.Token, progress, forceRefresh: true);
                using (dataset)
                {
                    if (!_firebase.CanPublishDataset || _firebase.UserId != user) return;
                    if (cached) throw new IOException("La descarga actual falló. Se conserva la publicación anterior y se reintentará.");
                    var json = dataset.Root.GetRawText();
                    using var parsed = System.Text.Json.JsonDocument.Parse(json);
                    var date = DateTimeOffset.Parse(parsed.RootElement.GetProperty("date").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                    if (DateTimeOffset.UtcNow - date >= TimeSpan.FromHours(24))
                        throw new IOException("El dataset de 7 días está desactualizado. Se reintentará al recuperar la conexión.");
                    await _firebase.PublishDatasetAsync(json, previous, _closed.Token);
                    CloudSyncStatus.Text = "Dataset Diamante+ de 7 días publicado en Firestore.";
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed.IsCancellationRequested) CloudSyncStatus.Text = "Publicación del dataset pendiente · " + ex.Message; }
        finally { _datasetPublishing = false; }
    }
}
