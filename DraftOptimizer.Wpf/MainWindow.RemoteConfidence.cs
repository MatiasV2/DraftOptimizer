using DraftOptimizer.Core;
using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DraftOptimizer;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer _confidenceTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _confidencePolling;
    private string ConfidenceCachePath => Path.Combine(_directory, "confidence-settings.json");

    private async Task InitializeConfidenceAsync()
    {
        try
        {
            if (File.Exists(ConfidenceCachePath))
                WinrateConfidence.Configure(Json.Read<double>(await File.ReadAllTextAsync(ConfidenceCachePath)));
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentOutOfRangeException) { }
        _confidenceTimer.Tick += async (_, _) => await PollConfidenceAsync();
        Closed += (_, _) => _confidenceTimer.Stop();
        await PollConfidenceAsync();
        if (!_closed.IsCancellationRequested) _confidenceTimer.Start();
    }

    private async Task PollConfidenceAsync()
    {
        if (_confidencePolling || _closed.IsCancellationRequested) return;
        _confidencePolling = true;
        try
        {
            using var timeout = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await _http.GetAsync(
                "https://firestore.googleapis.com/v1/projects/draftoptimizer/databases/(default)/documents/config/statistics",
                timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) return;
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var field = document.RootElement.GetProperty("fields").GetProperty("confidencePercent");
            double percent = field.TryGetProperty("doubleValue", out var number) ? number.GetDouble()
                : double.Parse(field.GetProperty("integerValue").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            bool changed = WinrateConfidence.Configure(percent);
            try
            {
                var temporary = ConfidenceCachePath + ".tmp";
                await File.WriteAllTextAsync(temporary, Json.Write(percent), timeout.Token);
                File.Move(temporary, ConfidenceCachePath, true);
            }
            catch (IOException) { }
            if (!changed) return;
            _aramStats.Clear();
            _loadout.Reset();
            _previewKey = null;
            _lastSuggestions = null;
            _draftBuildOptionsKey = null;
            RefreshSuggestions();
            UpdateDraftBuildOptionsForSelection();
            UpdateLoadoutPreview();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException or
            JsonException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException or
            FormatException or ArgumentOutOfRangeException or IOException) { }
        finally { _confidencePolling = false; }
    }
}
