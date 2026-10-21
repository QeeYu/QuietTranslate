using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace QuietTranslate;

internal sealed record ReadingPart(long Id, string Text, bool Stable, bool Closed, bool Interrupted = false);

// Display boundaries are local reading units, independent of API turns and visual wraps.
internal sealed class ReadingStream(Func<long> nextId, Action<ReadingPart> publish)
{
    private string text = "";
    private long id;
    private double started, updated, hint = double.NegativeInfinity;
    private bool stable;
    internal double Budget { get; set; } = 58;
    internal void Reset() { text = ""; id = 0; stable = false; hint = double.NegativeInfinity; }
    private static bool Filler(string value) => value.Trim('。', '！', '？', '.', '!', '?', ' ', '、', ',') is "嗯" or "啊" or "呃" or "哦" or "え" or "あ" or "うん";
    private static int Boundary(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            bool end = value[i] is '。' or '！' or '？' or '!' or '?';
            if (value[i] == '.' && (i + 1 == value.Length || char.IsWhiteSpace(value[i + 1])))
            {
                string word = value[..(i + 1)].Split(' ').Last().ToLowerInvariant();
                end = word is not ("mr." or "mrs." or "ms." or "dr." or "e.g." or "i.e.");
            }
            if (!end || Filler(value[..(i + 1)])) continue;
            while (i + 1 < value.Length && value[i + 1] is '”' or '’' or '」' or '』' or '"') i++;
            return i + 1;
        }
        return -1;
    }
    internal static bool SentenceEnded(string value) => value.Length > 0 && Boundary(value) == value.TrimEnd().Length;
    private int Limit()
    {
        double units = 0;
        for (int i = 0; i < text.Length; i++)
        {
            units += text[i] <= 127 ? .5 : 1;
            if (units >= Budget) return i + 1;
        }
        return text.Length;
    }
    private int SoftCut(int limit)
    {
        for (int i = Math.Min(limit, text.Length) - 1; i >= Math.Min(10, limit / 2); i--)
            if (text[i] is ',' or '，' or '、' or ';' or '；' || char.IsWhiteSpace(text[i])) return i + 1;
        return 0;
    }
    internal void Append(string delta, double now)
    {
        if (delta.Length == 0) return;
        if (id == 0) { id = nextId(); started = now; }
        text += delta.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        updated = now; hint = double.NegativeInfinity;
        while (text.Length > 0)
        {
            int boundary = Boundary(text), limit = Limit();
            if (boundary > 0 && boundary < text.TrimEnd().Length) { Close(boundary, now, notifyRemainder: false); continue; }
            int cut = limit < text.Length ? SoftCut(limit) : 0;
            if (cut > 0) { Close(cut, now, notifyRemainder: false); continue; }
            // No linguistic model locally: never claim a five-second timer is a sentence boundary.
            // Bound a pathological punctuationless stream, but normally wait for punctuation/silence.
            int maximum = (int)Math.Clamp(Budget * 4, 160, 400);
            if (text.Length <= maximum) break;
            if (char.IsLowSurrogate(text[maximum])) maximum--;
            Close(maximum, now, notifyRemainder: false);
        }
        Progress();
    }
    internal void ServerEnd(double now)
    {
        hint = now;
        if (text.Length > 0 && Boundary(text) == text.TrimEnd().Length) Close(text.Length, now);
    }
    internal void Tick(double now, double lastSound)
    {
        if (text.Length == 0) return;
        int boundary = Boundary(text);
        if (boundary > 0 && now - updated >= .25) Close(boundary, now);
        else if (now - Math.Max(updated, lastSound) >= 1.2) Close(text.Length, now);
        else if (now - started >= 5 && SoftCut(Limit()) is > 0 and var cut) Close(cut, now);
        else if (!stable && ((hint >= updated && now - updated >= .25) || now - started >= .65)) { stable = true; Progress(); }
    }
    internal void Flush(double now) { if (text.Length > 0) Close(text.Length, now, true); }
    private void Progress() { if (!string.IsNullOrWhiteSpace(text)) publish(new(id, text, stable, false)); }
    private void Close(int length, double now, bool interrupted = false, bool notifyRemainder = true)
    {
        string value = text[..length];
        if (!string.IsNullOrWhiteSpace(value)) publish(new(id, value, !interrupted, true, interrupted));
        text = text[length..].TrimStart(); id = text.Length > 0 ? nextId() : 0; started = now; stable = false; hint = double.NegativeInfinity;
        if (notifyRemainder) Progress();
    }
}

internal sealed class LiveCaptions
{
    internal static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    private readonly Func<long> nextId;
    private readonly Action<ReadingPart, bool> record;
    private readonly ReadingStream target, source;
    private readonly List<SpeechCue> cues = new();
    private readonly HashSet<long> pendingExpiry = new();
    private long targetPart, targetDisplay, previewId, previewSource, sourcePart, consumedSource, lateSourceOwner, sourceOwner, sourceOwnerPart;
    private string lastWire = "", preview = "", sourceText = "";
    private bool wireEnded, wireActive, sourceBlocked;
    private double lastSound, sourceUpdated, now;
    internal string LatestSource { get; private set; } = "";
    internal LiveCaptions(Func<long> nextId, Action<ReadingPart, bool> record)
    {
        this.nextId = nextId; this.record = record;
        source = new ReadingStream(nextId, part =>
        {
            sourcePart = part.Id; LatestSource = sourceText = part.Text; record(part, true);
            if (part.Id == sourceOwnerPart && cues.LastOrDefault(cue => cue.Id == sourceOwner) is { } assigned) Set(assigned with { Original = part.Text });
            if (sourceBlocked)
            {
                consumedSource = Math.Max(consumedSource, part.Id);
                if (cues.LastOrDefault(cue => cue.Id == lateSourceOwner) is { } owner) { Set(owner with { Original = part.Text }); sourceOwner = owner.Id; sourceOwnerPart = part.Id; }
                if (part.Closed) { sourceBlocked = false; lateSourceOwner = 0; }
            }
        }) { Budget = 65 };
        target = new ReadingStream(nextId, TargetPart);
    }
    internal SpeechCue[] Snapshot() => cues.ToArray();
    private void Set(SpeechCue cue)
    {
        int index = cues.FindIndex(value => value.Id == cue.Id);
        if (index >= 0) cues[index] = cue; else cues.Add(cue);
        while (cues.Count > 12) { pendingExpiry.Remove(cues[0].Id); cues.RemoveAt(0); }
    }
    private void TargetPart(ReadingPart part)
    {
        if (targetPart != part.Id)
        {
            targetPart = part.Id; targetDisplay = previewId != 0 ? previewId : part.Id;
            if (previewId != 0 && previewSource != 0) { sourceOwner = targetDisplay; sourceOwnerPart = previewSource; }
            consumedSource = Math.Max(consumedSource, previewId != 0 ? previewSource : sourcePart);
            if (sourcePart == 0 && lateSourceOwner == 0) { sourceBlocked = true; lateSourceOwner = targetDisplay; }
            previewId = previewSource = 0; preview = "";
        }
        string original = cues.LastOrDefault(cue => cue.Id == targetDisplay)?.Original ?? "";
        bool awaitingModel = part.Closed && !part.Interrupted && wireActive && !ReadingStream.SentenceEnded(part.Text);
        if (awaitingModel) pendingExpiry.Add(targetDisplay);
        double? finished = part.Closed && !awaitingModel ? now : null;
        Set(new(targetDisplay, original, part.Text, part.Stable || part.Closed, finished, part.Interrupted));
        record(part, false);
        if (part.Closed) targetPart = targetDisplay = 0;
    }
    internal void Source(string value, bool authoritative, double now)
    {
        this.now = now;
        if (authoritative) source.Append(value, now);
        // Consume only the source part claimed when a target part starts. A genuinely new
        // source part may already be arriving while the previous target is still streaming.
        if (sourceBlocked || (sourcePart != 0 && sourcePart <= consumedSource))
        {
            if (previewSource == sourcePart) RemovePreview();
            return;
        }
        if (!authoritative && sourcePart != 0 && !value.StartsWith(sourceText, StringComparison.Ordinal)) return;
        // Confirmed text belongs to its source reading part, not to the latest raw callback.
        // In particular a late suffix of an already translated part cannot start a new preview.
        preview = authoritative ? sourceText : value;
        sourceUpdated = now;
        if (preview.Length > 140) preview = preview[^140..];
        ShowPreview();
    }
    private void ShowPreview()
    {
        if (preview.Length == 0) return;
        if (previewId == 0) previewId = nextId();
        previewSource = sourcePart;
        Set(new(previewId, preview));
    }
    internal void Target(string cumulative, bool complete, double now)
    {
        this.now = now;
        if (cumulative.Length == 0) { target.Flush(now); FinishPending(); RemovePreview(); lastWire = ""; wireEnded = wireActive = false; sourceBlocked = true; return; }
        string delta = !wireEnded && cumulative.StartsWith(lastWire, StringComparison.Ordinal) ? cumulative[lastWire.Length..] : cumulative;
        lastWire = cumulative; wireEnded = complete; wireActive = !complete;
        target.Append(delta, now); if (complete) { target.ServerEnd(now); FinishPending(); }
    }
    internal void Activity(double value, double now) { if (value > .012) lastSound = now; }
    internal void SetBudget(double value) => target.Budget = Math.Clamp(value, 24, 100);
    internal void Tick(double now)
    {
        this.now = now;
        // A quiet playback device is not proof the model has finished returning a sentence.
        target.Tick(now, wireActive ? now : lastSound); source.Tick(now, lastSound);
        if (now - sourceUpdated >= 2.5) RemovePreview();
    }
    private void RemovePreview() { if (previewId != 0) cues.RemoveAll(cue => cue.Id == previewId); previewId = previewSource = 0; preview = ""; }
    private void FinishPending()
    {
        foreach (long id in pendingExpiry.ToArray())
            if (cues.LastOrDefault(cue => cue.Id == id) is { } cue) Set(cue with { CompletedAt = now });
        pendingExpiry.Clear();
    }
    internal void ClearHistory()
    {
        if (targetDisplay != 0 && cues.LastOrDefault(cue => cue.Id == targetDisplay) is { } current) Set(current with { Complete = true, CompletedAt = Now });
        target.Reset(); source.Reset(); pendingExpiry.Clear(); targetPart = targetDisplay = sourcePart = lateSourceOwner = sourceOwner = sourceOwnerPart = 0; LatestSource = sourceText = ""; sourceBlocked = true; RemovePreview();
    }
    internal void Flush(double now) { this.now = now; target.Flush(now); source.Flush(now); FinishPending(); RemovePreview(); }
}
