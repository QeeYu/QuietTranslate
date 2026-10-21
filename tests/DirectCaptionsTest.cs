using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuietTranslate;

internal static class DirectCaptionsTest
{
    private static void Check(bool value, string error) { if (!value) throw new Exception(error); }
    private static async Task Until(Func<bool> condition, string error)
    { for (int i = 0; i < 160 && !condition(); i++) await Task.Delay(25); Check(condition(), error); }
    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T item) yield return item; foreach (var item2 in FindAll<T>(child)) yield return item2; }
    }
    private static void Capture(FrameworkElement view, string path)
    {
        view.UpdateLayout(); var image = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(view);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); png.Save(file);
    }
    internal static async Task<int> RunAsync(string output)
    {
        Directory.CreateDirectory(output); var results = new List<object>(); int failed = 0;
        async Task Test(string name, Func<Task> run)
        { try { await run(); results.Add(new { name, passed = true }); } catch (Exception ex) { failed++; results.Add(new { name, passed = false, error = ex.Message }); } }
        await Test("Old paired-mode settings are ignored and no second translation service exists", () =>
        {
            var options = JsonSerializer.Deserialize<SpeechOptions>("{\"StrictPaired\":true,\"SentenceProvider\":0}")!;
            options.Validate(); string json = JsonSerializer.Serialize(options);
            Check(!json.Contains("StrictPaired") && !json.Contains("SentenceProvider"), "Old mode remained active");
            Check(typeof(LiveSession).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single().GetParameters().Length == 2, "A text translation callback still exists"); return Task.CompletedTask;
        });
        await Test("Transport fragments join; punctuationless text is not cut every five seconds or mid-word", () =>
        {
            long id = 0; var parts = new List<ReadingPart>(); var stream = new ReadingStream(() => ++id, parts.Add);
            stream.Append("人之间的", 0); stream.ServerEnd(0); stream.Tick(.4, .4); stream.Append("灵魂的联系", 1.1);
            Check(parts.Select(p => p.Id).Distinct().Count() == 1 && !parts.Any(p => p.Closed), "API turns split an unfinished phrase");
            stream.Append("会在这个时候慢慢建立起来", 2.2); stream.Tick(5.1, 5.1);
            Check(!parts.Any(p => p.Closed), "Five-second timer split a continuous phrase");
            stream.Append("。", 5.2); stream.ServerEnd(5.2);
            Check(parts.Last(p => p.Closed).Text == "人之间的灵魂的联系会在这个时候慢慢建立起来。", "Sentence was lost");
            stream.Append("嗯。", 6); stream.Append("下雨了。还想见你。", 6.1); stream.Tick(6.4, 6.4);
            var closed = parts.Where(p => p.Closed).ToArray(); Check(closed[^2].Text == "嗯。下雨了。" && closed[^1].Text == "还想见你。", "Short sentence or filler grouping failed");
            stream.Append("3.14 is a number.", 7); stream.Tick(7.3, 7.3); Check(parts.Last(p => p.Closed).Text == "3.14 is a number.", "Decimal was split");
            stream.Append("これは長い句読点のない日本語です話はまだ続いています", 8); stream.Tick(9.3, 9.3); Check(!parts.Last().Closed, "Network gap mistaken for silence"); stream.Tick(13.1, 13.1); Check(!parts.Last().Closed, "Japanese phrase was cut by a timer");
            stream.Tick(14.5, 13.1); Check(parts.Last().Closed, "Silence did not close the tail");
            var bounded = new List<ReadingPart>(); var longStream = new ReadingStream(() => ++id, bounded.Add);
            longStream.Append(new string('長', 900), 20);
            Check(bounded.All(p => p.Text.Length <= 400) && string.Concat(bounded.Where(p => p.Closed).Select(p => p.Text)) + bounded.Last().Text == new string('長', 900), "Pathological stream unbounded or text lost");
            return Task.CompletedTask;
        });
        await Test("New source previews survive the preceding streamed target; claimed tails do not become another row", () =>
        {
            long id = 0; var records = new List<(ReadingPart part, bool original)>(); var live = new LiveCaptions(() => ++id, (part, original) => records.Add((part, original)));
            live.Source("雨が降ってきた。", true, 0); long preview = live.Snapshot().Single().Id;
            live.Target("下", false, .1); Check(live.Snapshot().Single().Id == preview, "Preview replacement inserted a new row");
            live.Source("また会いたい", true, .2); live.Target("下雨了。", true, .3);
            var snapshot = live.Snapshot(); Check(snapshot.Any(c => c.Translation == "下雨了。" && c.Complete) && snapshot.Any(c => c.Translation.Length == 0 && c.Original == "また会いたい"), "A valid next source part was hidden or cleared by the preceding target");
            live.Target("还想见你", false, .4); Check(live.Snapshot().Any(c => c.Translation == "还想见你" && !c.Complete), "Draft target was prematurely final");
            live.Source("。", true, .5); Check(live.Snapshot().Any(c => c.Translation == "下雨了。"), "Late source overwrote stable target");
            live.Target("还想见你。", true, .6); live.Tick(.9);
            Check(live.Snapshot().All(c => c.Translation.Length > 0), "Buffered late source reappeared after finalization");
            live.Source("新しく話し始めた。", true, 1); Check(live.Snapshot().Single(c => c.Translation.Length == 0).Original == "新しく話し始めた。", "Fresh preview was lost or joined the old source");
            Check(records.Any(r => r.original && r.part.Text == "また会いたい。") && records.Any(r => !r.original && r.part.Text == "还想见你。"), "Independent source or target missing"); return Task.CompletedTask;
        });
        await Test("Multi-line lifetime is shared; only a fourth row scrolls and retired prefixes never return", () =>
        {
            var board = new CueRows(); bool Fits(string text, bool draft) => text.Length <= 10;
            var cue = new SpeechCue(1, "", new string('甲', 30)); var rows = board.Layout(new[] { cue }, 100, 5, true, Fits);
            Check(rows.Length == 3 && rows.All(row => row.Draft), "Draft rows missing"); cue = cue with { Complete = true, CompletedAt = 200 };
            rows = board.Layout(new[] { cue }, 204, 5, true, Fits); Check(rows.Length == 3 && rows.All(row => !row.Draft), "First line expired before its group");
            var next = new SpeechCue(2, "", "新句", true, 201);
            rows = board.Layout(new[] { cue, next }, 202, 5, true, Fits); Check(rows.Count(row => row.Id == 1) == 2 && rows.Last().Id == 2, "Fourth row scrolled too much");
            rows = board.Layout(new[] { cue, next }, 204.2, 5, true, Fits); Check(rows.Length == 3 && rows.Count(row => row.Id == 1) == 2, "Configured five seconds shortened or retired prefix returned");
            Check(board.Layout(new[] { cue, next }, 205.1, 5, true, Fits).Single().Id == 2, "Group did not share its full lifetime");
            Check(board.Layout(new[] { cue, next }, 206.1, 5, true, Fits).Length == 0, "Completed group never expired");
            var previewBoard = new CueRows(); int measured = 0;
            bool CountedFits(string text, bool draft) { measured++; return text.Length <= 10; }
            var pair = new[] { new SpeechCue(3, "", new string('乙', 20), true, 200), new SpeechCue(4, new string('原', 80)) };
            rows = previewBoard.Layout(pair, 201, 5, true, CountedFits); Check(rows.Length == 3 && rows.Count(row => row.Id == 3) == 2 && rows.Count(row => row.Source) == 1, "Original preview evicted stable translation rows");
            int before = measured; previewBoard.Layout(pair, 201.1, 5, true, CountedFits); Check(measured == before, "Unchanged text was measured repeatedly"); return Task.CompletedTask;
        });
        await Test("Readability settles within 650ms while expiry waits for actual closure", () =>
        {
            long id = 0; var live = new LiveCaptions(() => ++id, (_, _) => { }); var board = new CueRows();
            live.Target("不知不觉就长大了抬头看看天空燕子", false, 0);
            live.Activity(1, .64); live.Tick(.64); Check(!live.Snapshot().Single().Complete, "Draft interval unexpectedly skipped");
            live.Activity(1, .66); live.Tick(.66); Check(live.Snapshot().Single().Complete && live.Snapshot().Single().CompletedAt == null, "Style stabilization started expiry");
            live.Target("不知不觉就长大了抬头看看天空燕子在飞翔昨天哭着脸如果我", true, 6);
            live.Activity(1, 6.4); live.Tick(6.4);
            Check(live.Snapshot().Length == 1 && live.Snapshot().Single().CompletedAt == null, "API end split or timed an unfinished sentence");
            Check(board.Layout(live.Snapshot(), 30, 5, true, (_, _) => true).Length == 1, "An in-progress stable caption expired");
            live.Target("有翅膀的话。", true, 31); var final = live.Snapshot().Single();
            Check(final.CompletedAt == 31 && final.Translation.Contains("燕子在飞翔"), "Screenshot phrase split mid-word");
            Check(board.Layout(live.Snapshot(), 35.99, 5, true, (_, _) => true).Length == 1 && board.Layout(live.Snapshot(), 36, 5, true, (_, _) => true).Length == 0, "Full configured lifetime not respected");
            var shortBoard = new CueRows(); var shortCue = new[] { new SpeechCue(100, "", "短句。", true, 10) };
            Check(shortBoard.Layout(shortCue, 24.99, 15, true, (_, _) => true).Length == 1, "Fifteen-second setting was silently shortened");
            return Task.CompletedTask;
        });
        await Test("Draft-to-normal reflow leaves no blank row; source cannot evict three translations", () =>
        {
            var board = new CueRows(); bool Fits(string text, bool draft) => text.Length <= (draft ? 5 : 10);
            var cue = new SpeechCue(1, "", "十个文字会先占两行");
            board.Layout(new[] { cue }, 0, 5, true, Fits);
            var rows = board.Layout(new[] { cue with { Complete = true }, new SpeechCue(2, "", "新句") }, 1, 5, true, Fits);
            Check(rows.Length == 2 && rows.All(row => row.Text.Length > 0), "Reserved row created a middle gap");
            board.Clear();
            rows = board.Layout(new[] { new SpeechCue(3, "", new string('甲', 30)), new SpeechCue(4, "原文预览") }, 0, 5, true, (text, _) => text.Length <= 10);
            Check(rows.Length == 3 && rows.All(row => row.Id == 3), "Speculative source evicted a real translation");
            return Task.CompletedTask;
        });
        await Test("Late source suffix stays consumed after local and server sentence endings", () =>
        {
            foreach (bool complete in new[] { false, true })
            {
                long id = 0; var records = new List<ReadingPart>(); var live = new LiveCaptions(() => ++id, (part, original) => { if (original) records.Add(part); });
                live.Source("雨", true, 0); live.Target("下雨了。", complete, .1);
                live.Source("が降ってきた。", true, .2);
                Check(live.Snapshot().All(cue => cue.Translation.Length > 0), "Late suffix became a second-row preview");
                Check(live.Snapshot().Single().Original == "雨が降ってきた。", "Claimed original tail did not update its own cue");
                live.Tick(.6); if (!complete) live.Target("下雨了。", true, .7);
                live.Source("次の新しい原文", true, 1);
                Check(live.Snapshot().Last().Original == "次の新しい原文" && live.Snapshot().Last().Translation.Length == 0, "Consumed source leaked into a fresh preview");
                live.Source("雨が降ってきた。", false, 1.05);
                Check(live.Snapshot().Last().Original == "次の新しい原文", "An old interim snapshot overwrote a new source part");
                live.Target("下一句译文。", true, 1.1);
                Check(live.Snapshot().Last().Original == "次の新しい原文" && records.Any(part => part.Text == "雨が降ってきた。"), "Visual source attribution or original records lost");
            }
            long next = 0; var delayed = new LiveCaptions(() => ++next, (_, _) => { });
            delayed.Target("译文先到了。", true, 0); delayed.Source("晚到的上一句原文。", true, .1);
            Check(delayed.Snapshot().All(cue => cue.Translation.Length > 0), "Source arriving entirely after target was guessed as a new sentence");
            delayed.Tick(.5); delayed.Source("真正的新原文", true, .7);
            Check(delayed.Snapshot().Last().Original == "真正的新原文", "New source did not recover after the consumed boundary");
            return Task.CompletedTask;
        });
        await Test("Silent playback while the model is returning text never expires an unfinished sentence", () =>
        {
            long id = 0; var live = new LiveCaptions(() => ++id, (_, _) => { }); var board = new CueRows();
            live.Target("这句话仍然没有说完", false, 0); long cueId = live.Snapshot().Single().Id;
            live.Tick(1.4); live.Tick(7);
            Check(live.Snapshot().Single().CompletedAt == null && board.Layout(live.Snapshot(), 7, 5, true, (text, _) => text.Length <= 10).Length > 0, "Quiet audio prematurely finalized the model's pending text");
            live.Target("这句话仍然没有说完的", false, 7.1);
            Check(live.Snapshot().Single().Id == cueId && live.Snapshot().Single().Translation.StartsWith("这句话"), "Only one tail character survived");
            live.Target("这句话仍然没有说完的部分。", true, 8);
            Check(live.Snapshot().Single().CompletedAt == 8, "Actual model ending did not finalize");
            var clauses = new LiveCaptions(() => ++id, (_, _) => { }); clauses.SetBudget(24);
            string phrase = "这是一句还没有真正说完的长话，后面的部分还在继续生成而不是结束";
            clauses.Target(phrase, false, 0); clauses.Tick(10);
            Check(clauses.Snapshot().Length > 1 && clauses.Snapshot().All(cue => cue.CompletedAt == null), "A local comma/length cut started expiry before the model finished");
            clauses.Target(phrase + "。", true, 11);
            Check(clauses.Snapshot().All(cue => cue.CompletedAt == 11), "Deferred reading parts did not start expiry at the model ending");
            return Task.CompletedTask;
        });
        await Test("Unicode separators and whitespace cannot create empty caption slots", () =>
        {
            var board = new CueRows(); var cues = new[] { new SpeechCue(1, "", "第一行\r\n\u2028\u2029\u0085\t       第二行"), new SpeechCue(2, "\u200B\uFEFF \t\u2060") };
            var rows = board.Layout(cues, 0, 5, true, (text, _) => text.Length <= 4);
            Check(rows.Length == 2 && rows[0].Text == "第一行" && rows[1].Text == "第二行" && rows.All(row => !string.IsNullOrWhiteSpace(row.Text)), "Invisible separator/space became an empty middle row");
            Check(CueRows.InlineText("a\r\nb\u2028c") == "a b c", "Caption normalization concatenated words");
            return Task.CompletedTask;
        });
        await Test("A hanging character does not push out a full row; short next sentences get a bounded handoff", () =>
        {
            var board = new CueRows(); var cue = new SpeechCue(1, "", new string('甲', 30));
            var rows = board.Layout(new[] { cue }, 0, 5, true, (text, _) => text.Length <= 10);
            cue = cue with { Translation = cue.Translation + "乙" };
            rows = board.Layout(new[] { cue }, .1, 5, true, (text, _) => text.Length <= 10);
            Check(rows.Length == 3 && rows.All(row => row.Text.Length == 10), "One new character immediately evicted a complete row");
            cue = cue with { Complete = true, CompletedAt = 1 };
            rows = board.Layout(new[] { cue }, 1, 5, true, (text, _) => text.Length <= 10);
            Check(rows.Last().Text == "乙", "A finalized tail was withheld");
            board.Clear(); var overflow = new[] { new SpeechCue(10, "", new string('甲', 30), true, 0), new SpeechCue(11, "", "乙") };
            rows = board.Layout(overflow, 1, 5, true, (text, _) => text.Length <= 10);
            Check(rows.All(row => row.Id == 10) && rows.Length == 3, "A new one-character cue ejected a full existing row");
            overflow[1] = overflow[1] with { Translation = "乙乙乙乙" };
            Check(board.Layout(overflow, 1.1, 5, true, (text, _) => text.Length <= 10).Last().Id == 11, "A readable next cue was withheld");
            board.Clear(); var pair = new[] { new SpeechCue(2, "", "上一句", true, 0), new SpeechCue(3, "", "的") };
            Check(board.Layout(pair, 5.5, 5, true, (_, _) => true).Length == 2, "Previous line vanished leaving only one character");
            Check(board.Layout(pair, 7.01, 5, true, (_, _) => true).Single().Id == 3, "Handoff retained an old line indefinitely");
            return Task.CompletedTask;
        });
        await Test("Font controls preview, save, persist and apply without reconnecting the live session", async () =>
        {
            var legacy = JsonSerializer.Deserialize<SpeechOptions>("{}")!;
            Check(legacy.FontWeight == 400 && legacy.FontFamily == "Microsoft YaHei UI", "Old configurations retained forced semibold");
            await using var server = new SpeechTest.Server(binary: true, scripted: true);
            var session = new LiveSession(Path.Combine(output, "fonts"), SpeechTest.SyntheticCapture);
            SpeechWindow? view = null; SettingsWindow? settings = null; Settings? saved = null;
            try
            {
                await session.EnableAsync(new SpeechOptions { Endpoint = server.Address.AbsoluteUri, ControlsHidden = false }, "synthetic-key");
                view = new SpeechWindow(session, () => { }, () => { }, _ => Task.CompletedTask); view.Show();
                settings = new SettingsWindow(new Settings { Speech = session.Options!.Copy() }, value => { saved = value; session.ApplyAppearance(value.Speech); view.ApplyOptions(); }); settings.Show(); settings.ShowPage(6); await Task.Delay(150);
                Check(HoverTest.Find<TextBlock>(view, "SpeechCueRow0")!.FontWeight == FontWeights.Normal, "Default subtitle weight is not normal");
                string identity = session.Options!.ConnectionIdentity;
                HoverTest.Find<ComboBox>(settings, "SpeechFontFamily")!.SelectedItem = "Segoe UI";
                HoverTest.Find<ComboBox>(settings, "SpeechFontWeight")!.SelectedIndex = 0;
                HoverTest.Find<TextBox>(settings, "SpeechFontSize")!.Text = "28";
                var preview = HoverTest.Find<TextBlock>(settings, "SpeechFontPreview")!;
                await Until(() => preview.FontSize == 28 && preview.FontWeight == FontWeights.Light && preview.FontFamily.Source.StartsWith("Segoe UI"), "Font preview did not follow controls");
                Check(HoverTest.Find<ComboBox>(settings, "SpeechFontFamily")!.SelectionBoxItem?.ToString() == "Segoe UI", "Selected font name is blank");
                preview.BringIntoView(); settings.UpdateLayout(); Capture(settings, Path.Combine(output, "font-settings.png"));
                HoverTest.Find<Button>(settings, "SaveSettings")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(saved?.Speech.FontFamily == "Segoe UI" && saved.Speech.FontWeight == 300 && saved.Speech.FontSize == 28, "Font choices did not save");
                await server.Source("最初の原文。"); await server.Caption("正在返回的上一句");
                await Until(() => FindAll<TextBlock>(view).Any(block => block.Name.StartsWith("SpeechCueRow") && block.Text == "正在返回的上一句"), "Previous target not displayed");
                await server.Source("新しい原文です");
                await Until(() => FindAll<TextBlock>(view).Any(block => block.Name.StartsWith("SpeechCueRow") && block.Text.Contains("新しい原文") && block.FontStyle == FontStyles.Italic && block.Opacity == .55), "New original was hidden by the preceding target stream");
                Capture(view, Path.Combine(output, "restored-source-preview.png"));
                await server.Caption("。", true); await server.Caption("字体调整后的字幕。", true);
                await Until(() => FindAll<TextBlock>(view).Any(block => block.Name.StartsWith("SpeechCueRow") && block.Text == "字体调整后的字幕。"), "Original did not become the new target in its slot");
                var row = FindAll<TextBlock>(view).First(block => block.Name.StartsWith("SpeechCueRow") && block.Text == "字体调整后的字幕。");
                Check(row.FontSize == 28 && row.FontWeight == FontWeights.Light && row.FontFamily.Source.StartsWith("Segoe UI") && session.Options.ConnectionIdentity == identity && server.Connections == 1, "Font change reconnected or did not apply");
                var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(saved))!.Speech;
                var copied = new SpeechOptions(); copied.CopyAppearanceFrom(restored); copied.Validate();
                Check(copied.FontFamily == "Segoe UI" && copied.FontWeight == 300 && copied.FontSize == 28, "Appearance save/restore lost fonts");
                var invalid = copied.Copy(); invalid.FontFamily = "https://example.invalid/font.ttf";
                try { invalid.Validate(); throw new Exception("External font URL accepted"); } catch (ArgumentException) { }
                var fallback = copied.Copy(); fallback.FontFamily = "QuietTranslate Synthetic Missing Family"; session.ApplyAppearance(fallback); view.ApplyOptions();
                session.ApplyAppearance(copied); view.ApplyOptions(); Capture(view, Path.Combine(output, "font-subtitle.png"));
            }
            finally { settings?.Close(); view?.Close(); await session.DisableAsync(); }
        });
        await Test("Newest-first pagination covers every source and target record without reversing only one page", () =>
        {
            using var history = new SessionHistory(Path.Combine(output, "descending"));
            for (int i = 0; i < 85; i++)
            {
                history.Upsert(new SpeechRecord(DateTimeOffset.UnixEpoch.AddSeconds(i), "synthetic", "zh-Hans", "", "译文" + i, false, "translation", i));
                history.Upsert(new SpeechRecord(DateTimeOffset.UnixEpoch.AddSeconds(i), "synthetic", "auto", "原文" + i, "", false, "original", i));
            }
            foreach (bool original in new[] { false, true })
            {
                var all = Enumerable.Range(0, 3).SelectMany(page => history.ReadPage(page, original: original, newestFirst: true)).ToArray();
                Check(all.Length == 85 && all.Select(record => record.Context).SequenceEqual(Enumerable.Range(0, 85).Reverse().Select(i => (long)i)), "Descending pages omitted, repeated or misordered records");
            }
            Check(history.ReadPage(0).First().Context == 0 && history.ReadPage(0, newestFirst: true).First().Context == 84, "Storage index order changed or newest page is old");
            return Task.CompletedTask;
        });
        await Test("Only Live is required; immediate drafts, stable text, separate history, clear and off cleanup", async () =>
        {
            await using var server = new SpeechTest.Server(binary: true, scripted: true); string root = Path.Combine(output, "live");
            var session = new LiveSession(root, SpeechTest.SyntheticCapture); SpeechWindow? view = null; SpeechHistoryWindow? history = null;
            try
            {
                await session.EnableAsync(new SpeechOptions { Endpoint = server.Address.AbsoluteUri, ControlsHidden = false, WindowLeft = 100, WindowTop = 100 }, "synthetic-key");
                view = new SpeechWindow(session, () => { }, () => { }, _ => Task.CompletedTask); view.Show(); await Task.Delay(120);
                var row = HoverTest.Find<TextBlock>(view, "SpeechCueRow0")!;
                await server.Source("雨が降ってきた。 "); await Until(() => row.Text.Contains("雨が"), "Source did not preview"); Check(row.FontStyle == FontStyles.Italic && row.Opacity == .55, "Source not faded italic");
                await server.Caption("下雨了"); await Until(() => row.Text == "下雨了", "First target draft was delayed"); Check(row.FontStyle == FontStyles.Italic && row.Opacity == .7, "Incomplete target is not a draft"); Capture(view, Path.Combine(output, "direct-draft.png"));
                await server.Caption("。", true); await Until(() => row.Text == "下雨了。" && row.FontStyle == FontStyles.Normal, "Stable target not normal");
                await server.Source("また会いたい。"); await server.Caption("还想见你。", true); await Until(() => session.Records == 2, "Short sentence merged into paragraph");
                var targets = await session.ReadPageAsync(0); Check(targets.All(record => record.Original.Length == 0) && targets[0].Translation == "还想见你。" && targets[1].Translation == "下雨了。", "History made a false bilingual pairing or is not newest-first");
                Check((await session.ReadPageAsync(0, true)).Any(record => record.Original.Contains("また会いたい")), "Original records missing");
                history = new SpeechHistoryWindow(session); history.Show(); await Task.Delay(150);
                Check(FindAll<TextBox>(history).Count(box => box.Name == "HistoryTranslationText") == 2, "Translation records regrouped into a large block"); Capture(history, Path.Combine(output, "direct-history-target.png")); Capture(view, Path.Combine(output, "direct-final.png"));
                await server.Caption("最新记录应在最上方。", true);
                await Until(() => FindAll<TextBox>(history).FirstOrDefault(box => box.Name == "HistoryTranslationText")?.Text == "最新记录应在最上方。", "Newest record was not inserted at the top automatically");
                await server.Caption("正在更新的新记录");
                await Until(() => FindAll<TextBox>(history).FirstOrDefault(box => box.Name == "HistoryTranslationText")?.Text == "正在更新的新记录", "New pending record missing at top");
                await server.Caption("，已经完成。", true);
                await Until(() => FindAll<TextBox>(history).FirstOrDefault(box => box.Name == "HistoryTranslationText")?.Text == "正在更新的新记录，已经完成。", "Latest record did not refresh its streamed content");
                HoverTest.Find<ComboBox>(history, "HistoryStream")!.SelectedIndex = 1; await Until(() => FindAll<TextBox>(history).Any(box => box.Name == "HistoryOriginalText"), "Original stream not available"); var box = FindAll<TextBox>(history).First(box => box.Name == "HistoryOriginalText"); box.SelectAll(); Check(box.SelectedText.Length > 0, "Original not selectable"); Capture(history, Path.Combine(output, "direct-history.png"));
                await session.ClearHistoryAsync(); await Task.Delay(400); Check(session.Records == 0 && session.OriginalRecords == 0, "Clear restored old records");
                await server.Caption("清空后的新句。", true); await Until(() => session.Records == 1, "New post-clear sentence missing"); Check((await session.ReadPageAsync(0)).Single().Translation == "清空后的新句。", "Old prefix restored after clear");
                Check(server.Connections == 1 && server.Errors.IsEmpty, "Presentation reconnected Live");
                var bilingual = session.Options!.Copy(); bilingual.Bilingual = true; session.ApplyAppearance(bilingual); view.ApplyOptions();
                await server.Source("これは新しい原文です。");
                await Task.Delay(400); Check(session.CueSnapshot().Last(cue => cue.Translation.Length > 0).Original == "これは新しい原文です。", "Late source was not retained with its unclaimed target");
                await server.Source("これは次の新しい原文です。");
                await Until(() => FindAll<TextBlock>(view).Any(block => block.Name.StartsWith("SpeechCueRow") && block.Visibility == Visibility.Visible && block.Text.Contains("これは次の")), "Fresh bilingual source did not follow the translated rows");
                var blocks = Enumerable.Range(0, 3).Select(index => HoverTest.Find<TextBlock>(view, "SpeechCueRow" + index)!).ToArray();
                int visibleCount = blocks.Count(block => block.Visibility == Visibility.Visible);
                Check(blocks.Take(visibleCount).All(block => block.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(block.Text)) && blocks.Skip(visibleCount).All(block => block.Visibility == Visibility.Collapsed), "Fixed source slot left an empty middle row");
                Capture(view, Path.Combine(output, "direct-bilingual-contiguous.png"));
                await server.Caption("新的译文。", true);
                await Until(() => FindAll<TextBlock>(view).Any(block => block.Name.StartsWith("SpeechCueRow") && block.Text.Contains("新的译文")), "New target not displayed");
                await server.Caption("再下一句。", true);
                await Until(() => FindAll<TextBlock>(view).Any(block => block.Name.StartsWith("SpeechCueRow") && block.Text.Contains("再下一句")), "Next target not displayed");
                Check(FindAll<TextBlock>(view).Where(block => block.Name.StartsWith("SpeechCueRow") && block.Visibility == Visibility.Visible).All(block => !block.Text.Contains("これは")), "LatestSource leaked from the preceding cue into the next one");
            }
            finally { history?.Close(); view?.Close(); await session.DisableAsync(); }
            Check(!Directory.GetFiles(root, "*.qth").Any() && session.CueSnapshot().Length == 0, "Off retained records or display");
        });
        await Test("Every target language fits the compact transparent selector at minimum width", async () =>
        {
            await using var server = new SpeechTest.Server(scripted: true); var session = new LiveSession(Path.Combine(output, "languages"), SpeechTest.SyntheticCapture); SpeechWindow? view = null;
            try
            {
                await session.EnableAsync(new SpeechOptions { Endpoint = server.Address.AbsoluteUri, ControlsHidden = false, WindowWidth = 420 }, "synthetic-key"); view = new SpeechWindow(session, () => { }, () => { }, _ => Task.CompletedTask); view.Show(); await Task.Delay(120);
                var selector = HoverTest.Find<ComboBox>(view, "LiveTarget")!;
                foreach (string target in SpeechOptions.Languages)
                {
                    var options = session.Options!.Copy(); options.Target = target; session.ApplyAppearance(options); view.ApplyOptions(); view.UpdateLayout();
                    var content = (ContentPresenter)selector.Template.FindName("SpeechLanguageContent", selector);
                    var measured = new FormattedText(SpeechOptions.LanguageName(target), System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(selector.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), selector.FontSize, Brushes.White, 1);
                    Check(content.ActualWidth >= measured.WidthIncludingTrailingWhitespace && content.ActualHeight >= measured.Height && content.ActualHeight <= selector.ActualHeight, "Selected language clipped: " + target);
                    Check(selector.TranslatePoint(new Point(selector.ActualWidth, 0), view).X <= view.ActualWidth - 5 && ((SolidColorBrush)selector.Resources["UiInput"]).Color.A == 0, "Selector overflowed or regained a background");
                }
                var button = HoverTest.Find<Button>(view, "SpeechDiagnostics")!; Check(((SolidColorBrush)button.Background).Color.A == 0 && ((SolidColorBrush)button.Foreground).Color == Ui.Color("#338691"), "Icon transparency or color changed");
                Capture(view, Path.Combine(output, "compact-language.png"));
            }
            finally { view?.Close(); await session.DisableAsync(); }
        });
        await File.WriteAllTextAsync(Path.Combine(output, "direct-captions.json"), JsonSerializer.Serialize(new { passed = results.Count - failed, failed, results, realCloudCalls = false, userSettingsWritten = false }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }
}
