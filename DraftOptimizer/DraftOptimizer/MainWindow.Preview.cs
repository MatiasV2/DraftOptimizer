using DraftOptimizer.Core;
using Microsoft.UI.Xaml;
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
        var recommendation = SelectedDraftBuildOption(champion.Id, role);
        var prepared = _loadout.Prepared;
        var profile = recommendation?.Profile ?? (Matches(prepared) ? prepared : Matches(_editing) ? _editing : _profiles.Where(p => Matches(p)).OrderByDescending(p => p.UpdatedAt).FirstOrDefault());
        bool automatic = ReferenceEquals(profile, prepared) && profile != null;
        var state = automatic ? _loadout.Status : profile == null ? "Sin perfil. Al confirmar se cargará Lolalytics D2+ automáticamente." : "Perfil personal disponible. Se aplicará al confirmar este campeón.";
        var key = $"{champion.Id}|{role}|{state}|{profile?.Id}|{profile?.UpdatedAt}|{profile?.GetHashCode()}|{_data.Date}";
        if (_previewKey == key) return;
        _previewKey = key; _previewProfile = profile;
        PreviewTitle.Text = champion.Name + " - " + role;
        PreviewState.Text = state;
        PreviewSource.Text = profile == null ? "D2+ · región Global · posición seleccionada" : profile.Source + " · " + profile.Patch + (recommendation == null ? "" : $" · Runas: {recommendation.RuneWinrate:P1} ({recommendation.RuneGames:N0} games) · Build: {recommendation.BuildWinrate:P1} ({recommendation.BuildGames:N0} games) · límite IC95% ≥ {recommendation.ConfidenceScore:P1}");
        if (profile == null) { PreviewRunes.Text = "Pendientes de carga"; PreviewItems.Text = "La configuración aparecerá aquí al estar preparada."; return; }
        string Name(string table, int id) => _data.DisplayName(table, id);
        PreviewRunes.Text = profile.Perks.Length == 0 ? "Este perfil contiene solo objetos. Sus runas se completarán desde D2+ al confirmar." : string.Join("\n", profile.Perks.Select((id, i) => Name(i >= 6 ? "statShardData" : "runeData", id)));
        var items = new StringBuilder("Alternativas por posición: elige una opción y no repitas objetos.\n\n");
        if (profile.SkillPriority.Length > 0) items.AppendLine("Maxeo: " + string.Join(" > ", profile.SkillPriority) + "\n");
        foreach (var block in profile.Blocks)
        {
            items.AppendLine(ItemBlockText.CleanTitle(block.Name));
            foreach (var group in block.Items.GroupBy(id => id))
            {
                var slot = block.Name.StartsWith("Objeto ") && block.Name.Length > 7 && char.IsDigit(block.Name[7]) ? "item" + block.Name[7] : block.Name.StartsWith("Botas") ? "boots" : "start";
                var evidence = profile.ItemEvidence.FirstOrDefault(x => x.ItemId == group.Key && x.Slot == slot);
                items.Append("• ").Append(Name("itemData", group.Key));
                if (group.Count() > 1) items.Append(" ×").Append(group.Count());
                if (evidence != null) items.Append($" · confianza {evidence.ConfidenceLower:P1} · {evidence.Games:N0} partidas");
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



