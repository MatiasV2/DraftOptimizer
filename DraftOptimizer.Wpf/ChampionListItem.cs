using DraftOptimizer.Core;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DraftOptimizer;

public sealed class ChampionListItem : INotifyPropertyChanged
{
    private Champion _champion;
    private Role _role;
    private bool _favorite, _available, _canChange;
    private Suggestion? _recommendation;
    private ConfidencePlacement? _confidencePlacement;
    public ChampionListItem(Champion champion) => _champion = champion;
    public Champion Champion => _champion;
    private string _portraitPatch = "";
    public string PortraitUrl => $"https://ddragon.leagueoflegends.com/cdn/{_portraitPatch}/img/champion/{Champion.Key}.png";
    public void SetPortraitPatch(string patch) { if (_portraitPatch == patch) return; _portraitPatch = patch; Changed(nameof(PortraitUrl)); }
    public string ConfidenceRank => _confidencePlacement is { } confidence ? $"#{confidence.Rank}" : "—";
    public string ConfidenceTooltip => _confidencePlacement is { } confidence
        ? $"{(Recommendation?.HasDraftConfidence == true ? "Confianza del draft" : "Confianza global")} · {confidence.Score:F1} · puesto {confidence.Rank} de {confidence.Total}"
        : "Sin datos de confianza";
    public string ConfidenceColor
    {
        get
        {
            if (_confidencePlacement is not { ColorPosition: var position }) return "#A6B5C9";
            (int R, int G, int B) green = (70, 212, 155), amber = (244, 200, 97), red = (242, 113, 121);
            var from = position <= 0.5 ? green : amber;
            var to = position <= 0.5 ? amber : red;
            double blend = position <= 0.5 ? 2 * position : 2 * position - 1;
            int Mix(int start, int end) => (int)Math.Round(start + (end - start) * blend);
            return $"#{Mix(from.R, to.R):X2}{Mix(from.G, to.G):X2}{Mix(from.B, to.B):X2}";
        }
    }

    public void SetConfidencePlacement(ConfidencePlacement? confidence)
    {
        if (_confidencePlacement == confidence) return;
        _confidencePlacement = confidence;
        Changed(nameof(ConfidenceRank));
        Changed(nameof(ConfidenceTooltip));
        Changed(nameof(ConfidenceColor));
    }

    public Role Role => _role;
    public bool IsFavorite => _favorite;
    public bool Available => _available;
    public Suggestion? Recommendation => _recommendation;
    public bool CanChangeFavorites => _canChange;
    public string FavoriteStar => IsFavorite ? "★" : "☆";
    public string FavoriteAction => $"{(IsFavorite ? "Quitar" : "Añadir")} {Champion.Name} {(IsFavorite ? "de" : "a")} favoritos de {Role}";
    public string Label => !Available ? "No disponible en el draft actual" : Recommendation is { UsesGlobalConfidence: true, IsAram: true } aram
        ? $"WR ARAM {aram.BaseWinrate:P1} · {aram.Games:N0} partidas · confianza {aram.Score:F1}\nLolalytics Emerald+ · Wilson 99% {aram.ConfidenceInterval.Lower:P1}–{aram.ConfidenceInterval.Upper:P1}"
        : Recommendation is { UsesGlobalConfidence: true } global
        ? $"WR del rol {global.BaseWinrate:P1} · {global.Games:N0} partidas del rol · confianza global {global.Score:F1}\n{Role} · rango Wilson 99% {global.ConfidenceInterval.Lower:P1}–{global.ConfidenceInterval.Upper:P1} · sin picks"
        : Recommendation is { } stats
        ? $"WR draft {(stats.DraftWinrate.HasValue ? stats.DraftWinrate.Value.ToString("P1") : "—")} · confianza {(stats.HasDraftConfidence ? stats.Score.ToString("F1") : "—")}\nRango del draft {(stats.HasDraftConfidence ? $"{stats.ConfidenceInterval.Lower:P1}–{stats.ConfidenceInterval.Upper:P1}" : "sin draft/contexto")} · {stats.ContextSamples} pares · WR base {stats.BaseWinrate:P1}"
        : "Sin estadísticas suficientes para calcular confianza";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public void Update(Champion champion, Role role, bool favorite, bool available, Suggestion? recommendation, bool canChange)
    {
        bool actionChanged = false;
        if (_champion != champion) { _champion = champion; Changed(nameof(Champion)); Changed(nameof(PortraitUrl)); actionChanged = true; }
        if (_role != role) { _role = role; Changed(nameof(Role)); actionChanged = true; }
        if (_favorite != favorite) { _favorite = favorite; Changed(nameof(IsFavorite)); Changed(nameof(FavoriteStar)); actionChanged = true; }
        if (actionChanged) Changed(nameof(FavoriteAction));
        bool labelChanged = false;
        if (_available != available) { _available = available; Changed(nameof(Available)); labelChanged = true; }
        if (!EqualityComparer<Suggestion?>.Default.Equals(_recommendation, recommendation)) { _recommendation = recommendation; Changed(nameof(Recommendation)); Changed(nameof(ConfidenceTooltip)); labelChanged = true; }
        if (labelChanged) Changed(nameof(Label));
        if (_canChange != canChange) { _canChange = canChange; Changed(nameof(CanChangeFavorites)); }
    }
}
