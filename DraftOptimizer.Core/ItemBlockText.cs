using System.Globalization;
using System.Text.RegularExpressions;

namespace DraftOptimizer.Core;

/// <summary>Round-trips item blocks through the multiline editor, including WinUI CR line endings.</summary>
public static class ItemBlockText
{
    public static string CleanTitle(string title) => title.Replace(" · alternativas · confianza 95%", "").Replace(" · confianza 95%", "");
    public static string Format(IEnumerable<ItemBlock> blocks) => string.Join(Environment.NewLine,
        blocks.Select(b => FormatName(b.Name) + ": " + string.Join(", ", b.Items)));

    private static string FormatName(string name) => name.Trim() != name || name.IndexOfAny([':', '\r', '\n', '\\', '"', '\u2028', '\u2029']) >= 0
        ? Json.Write(name) : name;

    public static List<ItemBlock> Parse(string text, IEnumerable<ItemBlock>? previous = null)
    {
        var metadata = (previous ?? []).GroupBy(b => b.Name).ToDictionary(g => g.Key, g => new Queue<ItemBlock>(g), StringComparer.Ordinal);
        var result = new List<ItemBlock>();
        ItemBlock? current = null;
        var items = new List<int>();
        void Finish()
        {
            if (current == null) return;
            if (items.Count == 0) throw new InvalidDataException($"El bloque '{current.Name}' no tiene objetos.");
            current.Items = items.ToArray(); result.Add(current); items.Clear();
        }
        void Start(string name)
        {
            Finish();
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("El bloque necesita un nombre.");
            var old = metadata.TryGetValue(name, out var queue) && queue.Count > 0 ? queue.Dequeue() : null;
            current = new() { Name = name, HideIfSummonerSpell = old?.HideIfSummonerSpell ?? "", ShowIfSummonerSpell = old?.ShowIfSummonerSpell ?? "" };
        }
        using var reader = new StringReader(text.Replace("\u2028", "\n").Replace("\u2029", "\n"));
        int lineNumber = 0;
        while (reader.ReadLine() is { } raw)
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0) continue;
            string payload = line;
            if (line[0] == '"')
            {
                int end = -1;
                for (int i = 1; i < line.Length; i++)
                {
                    if (line[i] == '\\') { i++; continue; }
                    if (line[i] == '"') { end = i; break; }
                }
                if (end < 0) throw new InvalidDataException($"Nombre entre comillas incompleto en la línea {lineNumber}.");
                string name;
                try { name = Json.Read<string>(line[..(end + 1)]); }
                catch (System.Text.Json.JsonException ex) { throw new InvalidDataException($"Nombre inválido en la línea {lineNumber}.", ex); }
                var suffix = line[(end + 1)..].TrimStart();
                if (!suffix.StartsWith(':')) throw new InvalidDataException($"Falta ':' después del nombre en la línea {lineNumber}.");
                Start(name); payload = suffix[1..];
            }
            else if (line.IndexOf(':') is var colon && colon >= 0)
            {
                Start(line[..colon].Trim()); payload = line[(colon + 1)..];
            }
            else if (current == null) Start("Objetos");
            foreach (var token in Regex.Split(payload, @"[,\s]+"))
            {
                if (token.Length == 0) continue;
                if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                    throw new InvalidDataException($"Objeto inválido '{token}' en la línea {lineNumber}; usa IDs numéricos separados por comas, espacios o saltos de línea.");
                items.Add(id);
            }
        }
        Finish();
        if (result.Count == 0) throw new InvalidDataException("Incluye al menos un bloque de objetos.");
        return result;
    }

    public static string AppendItem(string text, int id)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Objetos: " + id;
        var last = text.TrimEnd()[^1];
        var separator = text.EndsWith('\r') || text.EndsWith('\n') || last is ':' or ',' ? "" : ", ";
        return text + separator + id;
    }
}
