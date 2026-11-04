using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using USM.Core.Services;
using USM.Core.Services.Diagnostics;
using USM.Core.Services.Localization;
using USM.Views;

namespace USM.Tests
{
    /// <summary>UI + What's New checks: <c>--ui</c>. Windows are never shown; all report text is fabricated.</summary>
    internal static class UiTests
    {
        private static int _failures;
        private static void Check(bool ok, string name, string detail = "")
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(ok || detail.Length == 0 ? "" : " -> " + detail)}");
            if (!ok) _failures++;
        }

        private static string OutDir()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "USM.slnx"))) d = d.Parent;
            if (d == null) throw new InvalidOperationException("Repo root (USM.slnx) not found");
            // Worktree lives in <...>/build/<name>; shared verify folder is <...>/build/issue1-verify.
            string root = d.Parent != null && d.Parent.Name.Equals("build", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(d.Parent.FullName, "issue1-verify")
                : Path.Combine(d.FullName, "build", "issue1-verify");
            return root;
        }

        public static int Run(string[] args)
        {
            Console.WriteLine("=== UI / What's New tests ===");
            ShouldShowTests();
            ChangelogTests();

            var t = new Thread(() =>
            {
                try { RunSta(); }
                catch (Exception ex) { Check(false, "STA UI run completed", ex.ToString()); }
                // WPF's hidden dispatcher window crashes the runtime when its STA thread ends normally; exit from here.
                Console.WriteLine(_failures == 0 ? "ALL UI TESTS PASSED" : $"{_failures} UI TEST(S) FAILED");
                Console.Out.Flush();
                Environment.Exit(_failures == 0 ? 0 : 1);
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            return 1;
        }

        private static void ShouldShowTests()
        {
            Console.WriteLine("-- ShouldShow --");
            Check(WhatsNewService.ShouldShow("1.0.0", "1.0.1", true), "older last seen shows");
            Check(WhatsNewService.ShouldShow("1.0.0", "1.0.1", false), "older last seen shows (no config flag)");
            Check(!WhatsNewService.ShouldShow("1.0.1", "1.0.1", true), "equal does not show");
            Check(!WhatsNewService.ShouldShow("1.0.2", "1.0.1", true), "newer last seen (downgrade) does not show");
            Check(!WhatsNewService.ShouldShow("1.1.0", "1.0.1", true), "newer minor does not show");
            Check(WhatsNewService.ShouldShow("1.0.9", "1.0.10", true), "numeric (not string) compare 1.0.9 < 1.0.10");
            Check(WhatsNewService.ShouldShow(null, "1.0.1", true), "no last seen + existing config (upgrade from 1.0) shows");
            Check(WhatsNewService.ShouldShow("", "1.0.1", true), "empty last seen + existing config shows");
            Check(WhatsNewService.ShouldShow("   ", "1.0.1", true), "blank last seen + existing config shows");
            Check(!WhatsNewService.ShouldShow(null, "1.0.1", false), "fresh install (no config) does not show");
            Check(!WhatsNewService.ShouldShow("", "1.0.1", false), "fresh install with empty value does not show");
            Check(!WhatsNewService.ShouldShow("garbage", "1.0.1", true), "unparsable last seen does not nag");
            Check(!WhatsNewService.ShouldShow("garbage", "1.0.1", false), "unparsable last seen, no config, does not show");
            Check(!WhatsNewService.ShouldShow("1.0.0", "garbage", true), "unparsable current does not show");
            Check(!WhatsNewService.ShouldShow("1.0.0", "", true), "empty current does not show");
            Check(!WhatsNewService.ShouldShow(null, "garbage", true), "unparsable current, no last seen, does not show");
            Check(WhatsNewService.ShouldShow("v1.0.0", "1.0.1", true), "'v' prefix accepted");
            Check(!WhatsNewService.ShouldShow("1.0.1-beta", "1.0.1", true), "suffix ignored: equal after trim");
            Check(WhatsNewService.ShouldShow("1.0", "1.0.1", true), "two-part version parsed");
            Check(!WhatsNewService.ShouldShow("1.0.1.0", "1.0.1", true), "1.0.1.0 equals 1.0.1");
            Check(WhatsNewService.ShouldShowNow("1.0.0", "1.0.1", true), "ShouldShowNow: entry exists");
            Check(!WhatsNewService.ShouldShowNow("1.0.0", "9.9.9", true), "ShouldShowNow: no changelog entry for current -> no");
        }

        private static void ChangelogTests()
        {
            Console.WriteLine("-- Changelog --");
            var entries = WhatsNewService.Entries;
            Check(entries.Count > 0, "changelog has entries");
            Check(WhatsNewService.HasEntry("1.0.1"), "has entry for 1.0.1");
            var versions = entries.Select(e => { WhatsNewService.TryParse(e.Version, out var v); return v; }).ToList();
            Check(versions.SequenceEqual(versions.OrderByDescending(v => v)), "entries are newest first");
            var keys = entries.SelectMany(e => e.Keys).ToList();
            Check(keys.Distinct().Count() == keys.Count, "changelog keys are unique");
            Check(entries.First(e => e.Version == "1.0.1").Keys.SequenceEqual(Enumerable.Range(1, 6).Select(i => $"WhatsNew_1_0_1_{i}")), "1.0.1 keys _1.._6");
            foreach (string lang in new[] { "en", "ar", "hi", "ja" })
            {
                LocalizationService.Instance.CurrentLanguage = lang;
            }
            LocalizationService.Instance.CurrentLanguage = "en";
            var missing = keys.Concat(AllNewKeys).Where(k => LocalizationService.T(k) == k).ToList();
            Check(missing.Count == 0, "all changelog/new keys exist in en", string.Join(",", missing));
        }

        private static readonly string[] AllNewKeys =
        {
            "Nav_ReportBug", "Nav_ReportBug_Tooltip", "ReportBug_Title", "ReportBug_Notice", "ReportBug_Generating",
            "ReportBug_Failed", "ReportBug_Trimmed", "ReportBug_Copy", "ReportBug_Copied", "ReportBug_CopyFailed",
            "ReportBug_OpenIssue", "ReportBug_OpenIssueFailed", "ReportBug_Close", "WhatsNew_Title",
            "WhatsNew_VersionHeader", "WhatsNew_Close", "Settings_WhatsNew", "Settings_WhatsNew_Tooltip",
        };

        private static readonly Regex KeyLike = new(@"^(Loc_)?[A-Z][A-Za-z0-9]*(_[A-Za-z0-9]+)+$", RegexOptions.Compiled);

        private static int RunSta()
        {
            string outDir = OutDir();
            string uiDir = Path.Combine(outDir, "ui");
            Directory.CreateDirectory(uiDir);
            string logDir = Path.Combine(outDir, "logs-ui");
            Directory.CreateDirectory(logDir);
            DiagnosticLog.Configure(logDir);

            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var app = new USM.App();
            app.InitializeComponent();
            Console.WriteLine("-- Windows (STA) --");

            string sample = "## USM bug report\n\n- USM version: v1.0.1\n- Windows: 10.0.19044\n- UI language: en\n\n### Last failed operation\n\nNone recorded.\n\n### Recent log\n\n```\n"
                + string.Join("\n", Enumerable.Range(1, 60).Select(i => $"2026-01-01 10:00:{i % 60:00} INFO Sample line {i} with a deliberately long fabricated message to test horizontal scrolling behaviour in the report box"))
                + "\n```\n";
            var report = new BugReportResult(sample, true);

            foreach (string lang in new[] { "en", "ar", "hi", "ja" })
            {
                LocalizationService.Instance.Initialize(lang);
                bool rtl = lang == "ar";

                // Report-a-bug window: normal and minimum size.
                foreach (var (w, h, tag) in new[] { (780.0, 620.0, "normal"), (520.0, 420.0, "min") })
                {
                    string name = $"ReportBug[{lang},{tag}]";
                    try
                    {
                        var win = new ReportBugWindow(() => report);
                        Pump(win.GenerateAsync());
                        var box = (TextBox)win.FindName("ReportBox");
                        Check(box.Text == sample, name + " shows injected report");
                        Check(box.FlowDirection == FlowDirection.LeftToRight, name + " report box stays LeftToRight");
                        Check(((FrameworkElement)win.Content).FlowDirection == (rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight), name + " flow direction follows language");
                        Check(((FrameworkElement)win.FindName("StatusText")).Visibility == Visibility.Visible, name + " trimmed note visible");
                        Check(((Button)win.FindName("CopyButton")).IsEnabled, name + " copy enabled after generation");
                        Inspect(win, name, w, h, Path.Combine(uiDir, $"reportbug_{lang}_{tag}.png"));
                    }
                    catch (Exception ex) { Check(false, name + " no exception", ex.ToString()); }
                }

                // Failure path.
                try
                {
                    var bad = new ReportBugWindow(() => throw new InvalidOperationException("fabricated failure"));
                    Pump(bad.GenerateAsync());
                    Check(bad.ReportText.Contains("fabricated failure") && !((Button)bad.FindName("CopyButton")).IsEnabled, $"ReportBug[{lang}] shows generation error and disables copy");
                }
                catch (Exception ex) { Check(false, $"ReportBug[{lang}] error path", ex.ToString()); }

                // What's New window (all entries).
                try
                {
                    var wn = new WhatsNewWindow();
                    Inspect(wn, $"WhatsNew[{lang}]", 560, 520, Path.Combine(uiDir, $"whatsnew_{lang}.png"));
                    Inspect(new WhatsNewWindow(), $"WhatsNew[{lang},min]", 380, 300, null);
                    var texts = Descendants(wn).OfType<TextBlock>().Select(t => t.Text).ToList();
                    int found = Enumerable.Range(1, 6).Count(i => texts.Contains(LocalizationService.T($"WhatsNew_1_0_1_{i}")));
                    Check(found == 6, $"WhatsNew[{lang}] lists all 6 notes", $"found {found}");
                }
                catch (Exception ex) { Check(false, $"WhatsNew[{lang}] no exception", ex.ToString()); }

                // Settings view (no DataContext: only XAML + resources).
                try
                {
                    var sv = new SettingsView();
                    Check(Descendants(sv).OfType<Button>().Any(b => (b.Content as string) == LocalizationService.T("Settings_WhatsNew")), $"Settings[{lang}] has localized What's new button");
                    Measure(sv, 1000, 1400);
                    NoUnresolved(sv, $"Settings[{lang}]");
                    if (lang == "en") Render(sv, 1000, 1400, Path.Combine(uiDir, "settings_en.png"));
                }
                catch (Exception ex) { Check(false, $"Settings[{lang}] no exception", ex.ToString()); }

                // New keys resolve (translation may fall back to en).
                Check(AllNewKeys.All(k => LocalizationService.T(k) != k), $"keys resolve in [{lang}]");
            }

            LocalizationService.Instance.Initialize("en");
            Check(WhatsNewService.ShouldShowNow(null, "1.0.1", true), "end-to-end decision sanity");
            DiagnosticLog.Configure(null);
            return 0;
        }

        private static void Pump(Task task)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(15))
            {
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(5);
            }
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            if (!task.IsCompleted) throw new TimeoutException("task did not complete");
            task.GetAwaiter().GetResult();
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                yield return cur;
                foreach (object child in LogicalTreeHelper.GetChildren(cur))
                    if (child is DependencyObject d) stack.Push(d);
                if (cur is Visual or System.Windows.Media.Media3D.Visual3D)
                    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(cur); i++)
                        stack.Push(VisualTreeHelper.GetChild(cur, i));
            }
        }

        private static void Measure(FrameworkElement root, double w, double h)
        {
            root.Measure(new Size(w, h));
            root.Arrange(new Rect(0, 0, w, h));
            root.UpdateLayout();
        }

        private static void NoUnresolved(DependencyObject root, string name)
        {
            var bad = new List<string>();
            foreach (var d in Descendants(root))
            {
                string? s = d switch { TextBlock t => t.Text, ContentControl c when c.Content is string cs => cs, _ => null };
                if (!string.IsNullOrEmpty(s) && KeyLike.IsMatch(s)) bad.Add(s);
            }
            if (root is Window w && string.IsNullOrWhiteSpace(w.Title)) bad.Add("<empty title>");
            foreach (var b in Descendants(root).OfType<Button>().Where(x => x.Content is string))
                if (string.IsNullOrWhiteSpace((string)b.Content)) bad.Add("<empty button>");
            Check(bad.Count == 0, name + " no unresolved Loc keys / empty labels", string.Join(",", bad));
        }

        private static void Inspect(Window win, string name, double w, double h, string? png)
        {
            var root = (FrameworkElement)win.Content;
            Measure(root, w - 0, h - 0);
            NoUnresolved(win, name);

            // Overflow: every button and the root-level texts must lie inside the window area.
            var overflow = new List<string>();
            if (Environment.GetEnvironmentVariable("USM_UI_DEBUG") == "1") Console.WriteLine($"    root {root.ActualWidth}x{root.ActualHeight} measured {w}x{h}");
            foreach (var fe in Descendants(root).OfType<FrameworkElement>().Distinct())
            {
                if (fe is not Button && !(fe is TextBlock tb && tb.TextWrapping == TextWrapping.Wrap && fe.Parent is Border)) continue;
                if (!fe.IsVisible && fe.Visibility != Visibility.Visible) continue;
                var r = fe.TransformToAncestor(root).TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
                if (r.Left < -1 || r.Top < -1 || r.Right > w + 1 || r.Bottom > h + 1)
                    overflow.Add($"{fe.GetType().Name}({(fe as Button)?.Content}) {r}");
                if (fe is Button bt && bt.ActualWidth > 0 && bt.DesiredSize.Width - bt.Margin.Left - bt.Margin.Right > bt.ActualWidth + 1)
                    overflow.Add($"button clipped: {bt.Content}");
            }
            Check(overflow.Count == 0, name + " no layout overflow", string.Join(" | ", overflow));
            if (png != null) Render(root, w, h, png);
        }

        private static void Render(FrameworkElement root, double w, double h, string path)
        {
            Measure(root, w, h);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle((Brush)Application.Current.FindResource("BrushBgMain"), null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(root)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(-root.Margin.Left, -root.Margin.Top, w, h),
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, w, h),
                }, null, new Rect(0, 0, w, h));
            }
            var bmp = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            enc.Save(fs);
            Check(new FileInfo(path).Length > 1000, "rendered " + Path.GetFileName(path));
        }
    }
}
