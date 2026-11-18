using DraftOptimizer.Core;
using System.Windows;
using System;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DraftOptimizer;
public sealed partial class MainWindow
{
    private BuildProfile? _previewProfile;
    private string? _previewKey;
    private void UpdateLoadoutPreview()
    {
        if (PreviewTitle == null || _data == null || _initializing) return;
        var champion = SelectedChampion;
        if (champion == null) return;
        var role = SelectedRole;
        bool Matches(BuildProfile? p) => p?.ChampionId == champion.Id && p.Role == role;
        var prepared = _loadout.Prepared;
        var profile = SelectedDraftBuildProfile(champion.Id, role) ?? (Matches(prepared) && prepared?.IsAram == _aramRoleMode ? prepared : null);
        if (profile is { Perks.Length: 0 } && prepared?.Id == profile.Id) profile = prepared;
        var recommendation = SelectedDraftBuildOption(champion.Id, role);
        if (!ReferenceEquals(recommendation?.Profile, profile)) recommendation = null;
        bool automatic = ReferenceEquals(profile, prepared) && profile != null;
        var state = automatic ? _loadout.Status : profile == null ? "Sin perfil. Al confirmar se cargará Lolalytics D2+ automáticamente." : recommendation != null ? "Opción de Lolalytics seleccionada." : "Perfil personal disponible. Se aplicará al confirmar este campeón.";
        var spells = profile?.SpellsFor(_favorites.FlashKey) ?? default;
        var key = $"{champion.Id}|{role}|{state}|{profile?.Id}|{profile?.UpdatedAt}|{spells.D}|{spells.F}|{profile?.GetHashCode()}|{_data.Date}";
        if (_previewKey == key) return;
        _previewKey = key; _previewProfile = profile;
        PreviewTitle.Text = champion.Name + " - " + role;
        PreviewState.Text = state;
        PreviewSource.Text = profile == null ? "D2+ · región Global · posición seleccionada" : profile.Source.Replace(" · Wilson 95%", "") + " · " + profile.Patch + (recommendation == null ? "" : $" · Runas: {recommendation.RuneWinrate:P1} ({recommendation.RuneGames:N0} games) · Build: {recommendation.BuildWinrate:P1} ({recommendation.BuildGames:N0} games) · límite IC99% ≥ {recommendation.ConfidenceScore:P1}");
        PreviewSpells.Text = profile == null ? "Pendientes de carga" : profile.Spell1Id == 0 ? (profile.Perks.Length == 0 ? "Se buscarán con las runas en Lolalytics al confirmar" : "Se mantienen los hechizos del cliente") :
            $"D · {_data.SummonerSpellName(spells.D)}   F · {_data.SummonerSpellName(spells.F)}";
        if (profile == null) { PreviewRunes.Text = "Pendientes de carga"; PreviewItems.Text = "La configuración aparecerá aquí al estar preparada."; return; }
        string Name(string table, int id) => _data.DisplayName(table, id);
        PreviewRunes.Text = profile.Perks.Length == 0 ? "Este perfil contiene solo objetos. Sus runas se completarán desde D2+ al confirmar." : string.Join("\n", profile.Perks.Select((id, i) => Name(i >= 6 ? "statShardData" : "runeData", id)));
        var items = new StringBuilder("Alternativas por posición: elige una opción y no repitas objetos.\n\n");
        if (profile.SkillPriority.Length > 0) items.AppendLine("Maxeo: " + string.Join(" > ", profile.SkillPriority) + "\n");
        foreach (var block in profile.Blocks)
        {
            items.AppendLine(ItemBlockText.CleanTitle(block.Name));
            var slot = block.Name.StartsWith("Objeto ") && block.Name.Length > 7 && char.IsDigit(block.Name[7]) ? "item" + block.Name[7]
                : block.Name.StartsWith("Botas") ? "boots"
                : block.Name.StartsWith("Alternativas · item") ? block.Name["Alternativas · ".Length..] : "start";
            foreach (var group in block.Items.GroupBy(id => id))
            {
                var evidence = profile.ItemEvidence.FirstOrDefault(x => x.ItemId == group.Key && x.Slot == slot);
                items.Append("• ").Append(Name("itemData", group.Key));
                if (group.Count() > 1) items.Append(" ×").Append(group.Count());
                if (evidence is { Games: > 0, Winrate: >= 0 and <= 1 })
                {
                    var interval = WinrateConfidence.Wilson99(evidence.Winrate * evidence.Games, evidence.Games);
                    items.Append($" · confianza 99% {interval.Lower:P1} · {evidence.Games:N0} partidas");
                }
                else if (slot != "start" && profile.Source.StartsWith("Lolalytics")) items.Append(" · muestra menor a 200 partidas");
                items.AppendLine();
            }
            items.AppendLine();
        }
        PreviewItems.Text = items.ToString();
    }
    private void PreviewEditClick(object sender, RoutedEventArgs e)
    {
        if (_previewProfile != null) { LoadProfile(Json.Read<BuildProfile>(Json.Write(_previewProfile))); MainTabs.SelectedItem = LoadoutTab; }
    }
}



