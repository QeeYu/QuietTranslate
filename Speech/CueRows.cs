using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace QuietTranslate;

internal sealed record SpeechCue(long Id, string Original, string Translation = "", bool Complete = false, double? CompletedAt = null, bool Failed = false);

internal sealed class CueRows
{
    internal static string InlineText(string value)
    {
        var text = new StringBuilder(value.Length); bool space = false;
        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character)) { space = text.Length > 0; continue; }
            if (char.IsControl(character) || character is '\u200B' or '\uFEFF' or '\u2060') continue;
            if (space) text.Append(' ');
            text.Append(character); space = false;
        }
        return text.ToString();
    }
    internal sealed record Row(long Id, string Text, bool Draft, bool Source, int End = 0);
    private readonly Dictionary<long, (int offset, bool source)> consumed = new();
    private readonly HashSet<long> retired = new();
    private readonly Dictionary<long, (string text, bool draft, double key, int offset, string[] lines, int[] starts)> cache = new();
    internal void Clear() { consumed.Clear(); retired.Clear(); cache.Clear(); }
    internal void InvalidateMeasurements() => cache.Clear();
    internal Row[] Layout(IReadOnlyList<SpeechCue> cues, double now, double lifetime, bool preview, Func<string, bool, bool> fits, double layoutKey = 0, int capacity = 3)
    {
        var present = cues.Select(c => c.Id).ToHashSet();
        foreach (long id in consumed.Keys.Where(id => !present.Contains(id)).ToArray()) consumed.Remove(id);
        foreach (long id in cache.Keys.Where(id => !present.Contains(id)).ToArray()) cache.Remove(id);
        retired.RemoveWhere(id => !present.Contains(id));
        var rows = new List<Row>();
        var targets = cues.Where(cue => cue.Translation.Length > 0).ToArray();
        var latest = targets.LastOrDefault();
        long waitingPrevious = latest is { CompletedAt: null } && InlineText(latest.Translation).Length < 8 && targets.Length > 1 ? targets[^2].Id : 0;
        foreach (var cue in cues)
        {
            if (cue.CompletedAt.HasValue && now - cue.CompletedAt.Value >= lifetime + (cue.Id == waitingPrevious ? 2 : 0)) { retired.Add(cue.Id); continue; }
            if (retired.Contains(cue.Id)) continue;
            bool source = cue.Translation.Length == 0, draft = !cue.Complete || cue.Failed;
            if (source && !preview) continue;
            string text = InlineText(source ? cue.Original : cue.Translation);
            if (text.Length == 0) continue;
            var progress = consumed.GetValueOrDefault(cue.Id);
            int offset = progress.source == source && progress.offset < text.Length ? progress.offset : 0;
            if (!cache.TryGetValue(cue.Id, out var measured) || measured.text != text || measured.draft != draft || measured.key != layoutKey || measured.offset != offset)
            {
            var wrapped = new List<string>(); var starts = new List<int>(); string line = ""; int lineStart = offset;
            var elements = StringInfo.GetTextElementEnumerator(text, offset);
            while (elements.MoveNext())
            {
                string element = elements.GetTextElement();
                if (line.Length > 0 && !fits(line + element, draft)) { if (!string.IsNullOrWhiteSpace(line)) { wrapped.Add(line.TrimEnd()); starts.Add(lineStart); } line = ""; }
                if (line.Length == 0 && string.IsNullOrWhiteSpace(element)) continue;
                if (line.Length == 0) lineStart = elements.ElementIndex;
                line += element;
            }
            if (!string.IsNullOrWhiteSpace(line)) { wrapped.Add(line.TrimEnd()); starts.Add(lineStart); }
            if (source && wrapped.Count > 1)
            {
                // A speculative source preview must not evict two stable target rows.
                int start = text.Length; var positions = StringInfo.ParseCombiningCharacters(text);
                for (int i = positions.Length - 1; i >= 0; i--)
                    if (fits((i > 0 ? "…" : "") + text[positions[i]..], draft)) start = positions[i]; else break;
                wrapped.Clear(); wrapped.Add((start > 0 ? "…" : "") + text[start..]);
                starts.Clear(); starts.Add(start);
            }
            measured = (text, draft, layoutKey, offset, wrapped.ToArray(), starts.ToArray()); cache[cue.Id] = measured;
            }
            var lines = measured.lines;
            int count = lines.Length;
            int skip = Math.Max(0, count - capacity);
            // Do not eject a full first row just because one new character wrapped below it.
            if (!source && cue.CompletedAt == null && count == capacity + 1 && lines[^1].Length < 4) skip = 0;
            consumed[cue.Id] = (skip > 0 ? measured.starts[skip] : offset, source);
            for (int i = skip; i < Math.Min(count, skip + capacity); i++)
                rows.Add(new(cue.Id, lines[i], draft, source, i + 1 < count ? measured.starts[i + 1] : text.Length));
        }
        // Preview is optional: it must never roll a real translation out of the three rows.
        int previewSpace = Math.Max(0, capacity - rows.Count(row => !row.Source));
        foreach (var row in rows.Where(row => row.Source).Reverse().Skip(previewSpace).ToArray()) rows.Remove(row);
        if (rows.Count > capacity && latest is { CompletedAt: null } && rows[^1] is { Source: false } tail && tail.Id == latest.Id && tail.Text.Length < 4 && rows[^2].Id != tail.Id)
            rows.RemoveAt(rows.Count - 1);
        while (rows.Count > capacity)
        {
            var removed = rows[0]; long id = removed.Id; rows.RemoveAt(0);
            consumed[id] = (removed.End, removed.Source);
            if (rows.All(row => row.Id != id)) retired.Add(id);
        }
        return rows.ToArray();
    }
}
