using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using VirtualKey = Windows.System.VirtualKey;
using WinUIEx;
using XrayUI.Helpers;
using XrayUI.Models;
using XrayUI.Services;

namespace XrayUI.Views
{
    public sealed partial class LogWindow
    {
        // UI-update throttle: burst traffic (many lines/sec) collapses into
        // at most 1 re-render per interval instead of one per line.
        private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);

        private static readonly SolidColorBrush RunningBrush =
            new(Windows.UI.Color.FromArgb(255, 34, 197, 94));   // green
        private static readonly SolidColorBrush StoppedBrush =
            new(Windows.UI.Color.FromArgb(255, 156, 163, 175)); // grey

        private readonly XrayService     _xray;
        private readonly SettingsService _settings;
        private readonly Func<Task> _reapplyConfigAsync;
        private readonly DispatcherQueue _queue;
        private readonly DispatcherQueueTimer _flushTimer;

        // Set from background thread when new lines arrive; consumed on UI thread.
        private volatile bool _dirty;
        private int _linesReceivedSinceFlush; // Background-thread increments; UI thread reads + clears.
        private int _prevBufferCount;
        private IReadOnlyList<string> _renderedLines = Array.Empty<string>();

        // Timestamp brush, re-resolved (with a full re-render) when the theme changes.
        private Brush _timestampBrush = null!;

        // Bumped whenever the core starts or stops, so a route test still in flight when the
        // core restarts is dropped instead of reporting the previous core's rules.
        private int _routeTestGeneration;

        // ScrollView.ScrollTo has no "keep current offset" sentinel like
        // ChangeView's null — the current offset is passed explicitly, and
        // jumps must disable animation to match the old instant behavior.
        private static readonly ScrollingScrollOptions JumpOptions =
            new(ScrollingAnimationMode.Disabled);

        public LogWindow(
            XrayService xray,
            SettingsService settings,
            Func<Task> reapplyConfigAsync)
        {
            this.InitializeComponent();
            _xray               = xray;
            _settings           = settings;
            _reapplyConfigAsync = reapplyConfigAsync;
            _queue              = DispatcherQueue.GetForCurrentThread();

            this.SetWindowSize(900, 600);
            AppWindow.Title = L.Log_Title;
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppTitleBar.Title = L.Log_Title;
            ThemeHelper.FollowAppTheme(this, WindowRoot);
            SystemBackdrop = new MicaBackdrop();

			ToolTipService.SetToolTip(LogPrivacyButton, L.Log_PrivacyTooltip);
            MaskAddressSubMenu.Text = L.Log_IpMask;
            MaskOffMenuItem.Text    = L.Log_MaskOff;
            LogLevelSubMenu.Text    = L.Log_Level;
            DnsLogSubMenu.Text      = L.Log_DnsLog;
            DnsLogOffMenuItem.Text  = L.Log_MaskOff;
            DnsLogOnMenuItem.Text   = L.Log_DnsLogOn;
            AutoScrollToggle.Content = L.Log_AutoScroll;
            CopyButton.Content       = L.Log_CopyAll;
            ClearButton.Content      = L.Log_Clear;

            ToolTipService.SetToolTip(RouteTestButton, L.RouteTest_Title);
            AutomationProperties.SetName(RouteTestButton, L.RouteTest_Title);
            RouteTestTitle.Text              = L.RouteTest_Title;
            RouteTestInput.PlaceholderText   = L.RouteTest_Placeholder;
            RouteTestRunButton.Content       = L.RouteTest_Run;
            RouteTestProxyText.Text          = L.RouteTest_Proxy;
            RouteTestDirectText.Text         = L.RouteTest_Direct;
            RouteTestBlockText.Text          = L.RouteTest_Block;

            _xray.LogReceived     += OnLogReceived;
            _xray.RunningChanged  += OnRunningChanged;
            WindowRoot.ActualThemeChanged += OnActualThemeChanged;

            RefreshTimestampBrush();
            RenderLog();
            UpdateStatus();
            ResetRouteTest();
            _ = InitializeLogSettingsMenuAsync();

            _flushTimer = _queue.CreateTimer();
            _flushTimer.Interval = FlushInterval;
            _flushTimer.IsRepeating = true;
            _flushTimer.Tick += OnFlushTick;
            _flushTimer.Start();

            this.Closed += OnClosed;
        }

        // ── Event handlers ─────────────────────────────────────────────────────

        private void OnClosed(object sender, WindowEventArgs args)
        {
            _flushTimer.Stop();
            _xray.LogReceived    -= OnLogReceived;
            _xray.RunningChanged -= OnRunningChanged;
            WindowRoot.ActualThemeChanged -= OnActualThemeChanged;
        }

        private void OnLogReceived(object? sender, string line)
        {
            // Called from background thread. Do NOT touch the UI here —
            // just mark dirty; the timer will re-render on the UI thread.
            Interlocked.Increment(ref _linesReceivedSinceFlush);
            _dirty = true;
        }

        private void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            // Enqueue so TimestampBrushSource's {ThemeResource} lookup has settled
            // before the brush is re-read; existing Runs keep their old brush
            // instance, so a full re-render is required.
            _queue.TryEnqueue(() =>
            {
                RefreshTimestampBrush();
                var offset = LogScrollViewer.VerticalOffset;
                RenderLog(forceRebuild: true);
                // Flush layout so ScrollableHeight reflects the rebuilt content
                // before ScrollTo clamps against it.
                LogScrollViewer.UpdateLayout();
                LogScrollViewer.ScrollTo(
                    LogScrollViewer.HorizontalOffset,
                    AutoScrollToggle.IsChecked == true ? LogScrollViewer.ScrollableHeight : offset,
                    JumpOptions);
            });
        }

        private void OnRunningChanged(object? sender, bool running)
        {
            _queue.TryEnqueue(() =>
            {
                UpdateStatus();
                ResetRouteTest();
            });
        }

        private void OnFlushTick(DispatcherQueueTimer sender, object args)
        {
            if (!_dirty) return;
            _dirty = false;

            var autoScroll = AutoScrollToggle.IsChecked == true;
            var prevOffset = LogScrollViewer.VerticalOffset;
            var prevExtent = LogScrollViewer.ExtentHeight;
            var prevCount  = _prevBufferCount;
            var received   = Interlocked.Exchange(ref _linesReceivedSinceFlush, 0);

            RenderLog();
            var newCount = _prevBufferCount; // RenderLog just updated this.

            if (autoScroll)
            {
                // Flush layout first so ScrollableHeight already includes the
                // lines just rendered — ScrollTo clamps at request time, unlike
                // ChangeView's late clamping.
                LogScrollViewer.UpdateLayout();
                LogScrollViewer.ScrollTo(
                    LogScrollViewer.HorizontalOffset,
                    LogScrollViewer.ScrollableHeight,
                    JumpOptions);
            }
            else
            {
                // Ring buffer evicted some lines: (received) − (net buffer growth) = lines pushed out.
                // Shift the scroll offset down by that height so visible content stays anchored.
                var evicted = Math.Max(0, received - (newCount - prevCount));
                if (evicted > 0 && prevCount > 0 && prevExtent > 0)
                {
                    var lineHeight = prevExtent / prevCount;
                    var target = Math.Max(0, prevOffset - evicted * lineHeight);
                    LogScrollViewer.ScrollTo(LogScrollViewer.HorizontalOffset, target, JumpOptions);
                }
            }
        }

        // ── Rendering ──────────────────────────────────────────────────────────

        private void RenderLog(bool forceRebuild = false)
        {
            // XrayService owns the single source of truth; we just render a snapshot.
            var lines = _xray.GetLogBuffer();
            var overlap = forceRebuild ? 0 : FindSuffixPrefixOverlap(_renderedLines, lines);
            var removed = _renderedLines.Count - overlap;

            if (forceRebuild)
            {
                LogTextBlock.Inlines.Clear();
            }
            else
            {
                for (var i = _renderedLines.Count; i > overlap; i--)
                {
                    LogTextBlock.Inlines.RemoveAt(0);
                }

                // Every non-first Span owns its leading line break. If the ring
                // buffer evicted the old first line, promote the retained Span.
                if (removed > 0 && overlap > 0 &&
                    LogTextBlock.Inlines[0] is Span firstLine &&
                    firstLine.Inlines.Count > 0 &&
                    firstLine.Inlines[0] is LineBreak)
                {
                    firstLine.Inlines.RemoveAt(0);
                }
            }

            for (var i = overlap; i < lines.Count; i++)
            {
                LogTextBlock.Inlines.Add(BuildLineSpan(
                    lines[i],
                    prependLineBreak: LogTextBlock.Inlines.Count > 0));
            }

            _renderedLines = lines;
            LineCountText.Text = Loc.Format("Log_Lines", lines.Count);
            _prevBufferCount = lines.Count;
        }

        private static int FindSuffixPrefixOverlap(
            IReadOnlyList<string> previous,
            IReadOnlyList<string> current)
        {
            for (var overlap = Math.Min(previous.Count, current.Count); overlap > 0; overlap--)
            {
                var previousStart = previous.Count - overlap;
                var matches = true;

                for (var i = 0; i < overlap; i++)
                {
                    if (!string.Equals(previous[previousStart + i], current[i], StringComparison.Ordinal))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    return overlap;
                }
            }

            return 0;
        }

        private Span BuildLineSpan(string line, bool prependLineBreak)
        {
            var span = new Span();
            var timestampLength = LogLineParser.TimestampLength(line);

            if (prependLineBreak)
            {
                span.Inlines.Add(new LineBreak());
            }

            if (timestampLength > 0)
            {
                span.Inlines.Add(new Run { Text = line[..timestampLength], Foreground = _timestampBrush });
            }

            if (timestampLength < line.Length)
            {
                span.Inlines.Add(new Run { Text = line[timestampLength..] });
            }

            return span;
        }

        private void RefreshTimestampBrush()
        {
            _timestampBrush = TimestampBrushSource.Foreground;
        }

        private async Task InitializeLogSettingsMenuAsync()
        {
            try
            {
                var settings = await _settings.LoadSettingsAsync();
                SetMaskAddressSelection(LogMaskAddress.Normalize(settings.LogMaskAddress));
                SetLogLevelSelection(XrayLogLevel.Normalize(settings.XrayLogLevel));
                SetDnsLogSelection(settings.DnsLog);
            }
            catch
            {
                SetMaskAddressSelection(LogMaskAddress.Off);
                SetLogLevelSelection(XrayLogLevel.Warning);
                SetDnsLogSelection(false);
            }
        }

        private void SetMaskAddressSelection(string value)
        {
            MaskOffMenuItem.IsChecked     = value == LogMaskAddress.Off;
            MaskQuarterMenuItem.IsChecked = value == LogMaskAddress.Quarter;
            MaskHalfMenuItem.IsChecked    = value == LogMaskAddress.Half;
            MaskFullMenuItem.IsChecked    = value == LogMaskAddress.Full;
        }

        private void SetLogLevelSelection(string value)
        {
            LogLevelDebugMenuItem.IsChecked   = value == XrayLogLevel.Debug;
            LogLevelInfoMenuItem.IsChecked    = value == XrayLogLevel.Info;
            LogLevelWarningMenuItem.IsChecked = value == XrayLogLevel.Warning;
        }

        private void SetDnsLogSelection(bool value)
        {
            DnsLogOffMenuItem.IsChecked = !value;
            DnsLogOnMenuItem.IsChecked  = value;
        }

        private void UpdateStatus()
        {
            var running = _xray.IsRunning;
            StatusText.Text = running ? L.Log_Running : L.Log_NotRunning;
            StatusDot.Fill  = running ? RunningBrush : StoppedBrush;
        }

        // ── Route test ─────────────────────────────────────────────────────────

        /// <summary>
        /// Enables the tester only while a core that exposes the route-test API is running, and
        /// drops any shown result: it described the rules of a core that is gone. The typed
        /// target stays, so a test can be rerun right after a reapply.
        /// </summary>
        private void ResetRouteTest()
        {
            _routeTestGeneration++;

            var running   = _xray.IsRunning;
            var available = running && _xray.RouteTest is not null;
            RouteTestInput.IsEnabled     = available;
            RouteTestRunButton.IsEnabled = available;

            ShowRouteTestState(null, available
                ? null
                : running ? L.RouteTest_ApiOwnedByProfile : L.RouteTest_NotRunning);
        }

        private async Task RunRouteTestAsync()
        {
            // The run button doubles as the busy flag: it is disabled while a test is in flight,
            // and the input (hence Enter) is disabled whenever the tester is unavailable.
            var endpoint = _xray.RouteTest;
            if (!RouteTestRunButton.IsEnabled || endpoint is null) return;

            if (!RouteTestProtocol.TryParseTarget(RouteTestInput.Text, out var target))
            {
                ShowRouteTestState(null, L.RouteTest_InvalidInput);
                return;
            }

            var generation = _routeTestGeneration;
            RouteTestRunButton.IsEnabled = false;
            ShowRouteTestState(null, null, busy: true);

            var result = await RouteTestService.TestAsync(endpoint, target);
            if (generation != _routeTestGeneration) return;

            RouteTestRunButton.IsEnabled = true;
            ShowRouteTestResult(result);
        }

        private void ShowRouteTestResult(RouteTestResult result)
        {
            if (result.Outcome == RouteTestOutcome.Failed)
            {
                ShowRouteTestState(null, Loc.Format("RouteTest_Failed", result.Detail));
                return;
            }

            var tag = result.Detail;
            var kind = RouteTestProtocol.Classify(tag);
            FrameworkElement badge = kind switch
            {
                RouteTestOutboundKind.Proxy  => RouteTestProxyBadge,
                RouteTestOutboundKind.Direct => RouteTestDirectBadge,
                RouteTestOutboundKind.Block  => RouteTestBlockBadge,
                _                            => RouteTestOtherBadge,
            };
            // A tag XrayUI did not name (e.g. a balancer's pick) is its own badge label.
            RouteTestOtherText.Text = tag;

            var detail = result.Outcome == RouteTestOutcome.NoRuleMatched
                ? Loc.Format("RouteTest_NoRuleMatched", tag)
                : kind == RouteTestOutboundKind.Other ? null : Loc.Format("RouteTest_OutboundTag", tag);
            ShowRouteTestState(badge, detail);
        }

        /// <param name="badge">The outcome badge to show, or null for none.</param>
        private void ShowRouteTestState(FrameworkElement? badge, string? detail, bool busy = false)
        {
            var hasMarker = busy || badge is not null;

            RouteTestProgress.IsActive   = busy;
            RouteTestProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            foreach (var element in new FrameworkElement[]
                     { RouteTestProxyBadge, RouteTestDirectBadge, RouteTestBlockBadge, RouteTestOtherBadge })
            {
                element.Visibility = element == badge ? Visibility.Visible : Visibility.Collapsed;
            }

            // ColumnSpacing applies even when the badge column is empty, which would indent a
            // message-only row against the input above it.
            RouteTestResultRow.ColumnSpacing = hasMarker ? 8 : 0;
            RouteTestDetail.Text = detail ?? string.Empty;
            RouteTestResultRow.Visibility = hasMarker || !string.IsNullOrEmpty(detail)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void RouteTestFlyout_Opened(object sender, object e)
        {
            RouteTestInput.Focus(FocusState.Programmatic);
            RouteTestInput.SelectAll();
        }

        private void RouteTestInput_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter) return;

            e.Handled = true;
            _ = RunRouteTestAsync();
        }

        private void RouteTestRunButton_Click(object sender, RoutedEventArgs e) => _ = RunRouteTestAsync();

        // ── Button handlers ────────────────────────────────────────────────────

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            var dp = new DataPackage();
            dp.SetText(string.Join('\n', _xray.GetLogBuffer()));
            Clipboard.SetContent(dp);
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            _xray.ClearLogBuffer();
            RenderLog();
        }

        private async void MaskAddressMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioMenuFlyoutItem item)
            {
                return;
            }

            // The off item carries the sentinel tag "off" (an empty-string Tag round-trips
            // unreliably through XAML); Normalize maps it back to Off ("").
            var value = LogMaskAddress.Normalize(item.Tag as string);
            SetMaskAddressSelection(value);

            await ApplyLogSettingAsync(L.Log_PrivacyTitle, s =>
            {
                if (LogMaskAddress.Normalize(s.LogMaskAddress) == value)
                {
                    return false;
                }

                s.LogMaskAddress = value;
                return true;
            });
        }

        private async void LogLevelMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioMenuFlyoutItem item)
            {
                return;
            }

            var value = XrayLogLevel.Normalize(item.Tag as string);
            SetLogLevelSelection(value);

            await ApplyLogSettingAsync(L.Log_Level, s =>
            {
                if (XrayLogLevel.Normalize(s.XrayLogLevel) == value)
                {
                    return false;
                }

                s.XrayLogLevel = value;
                return true;
            });
        }

        private async void DnsLogMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioMenuFlyoutItem item)
            {
                return;
            }

            var value = (item.Tag as string) == "on";
            SetDnsLogSelection(value);

            await ApplyLogSettingAsync(L.Log_DnsLog, s =>
            {
                if (s.DnsLog == value)
                {
                    return false;
                }

                s.DnsLog = value;
                return true;
            });
        }

        /// <summary>
        /// Shared save path for the log-settings flyout: persists the mutation, then hot-reapplies
        /// the config (in TUN mode that rebuilds the session).
        /// <paramref name="apply"/> returns false when the stored value already matches.
        /// </summary>
        private async Task ApplyLogSettingAsync(string title, Func<AppSettings, bool> apply)
        {
            try
            {
                var settings = await _settings.LoadSettingsAsync();
                if (!apply(settings))
                {
                    return;
                }

                await _settings.SaveSettingsAsync(settings);

                if (!_xray.IsRunning)
                {
                    return;
                }

                await _reapplyConfigAsync();
            }
            catch (Exception ex)
            {
                await ShowInfoAsync(title, Loc.Format("Log_PrivacyFailed", ex.Message));
            }
        }

        private async Task ShowInfoAsync(string title, string message)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                RequestedTheme = ThemeHelper.ActualTheme,
                Title = title,
                Content = message,
                CloseButtonText = L.Dialog_OK
            };

            await dialog.ShowAsync();
        }
    }
}
