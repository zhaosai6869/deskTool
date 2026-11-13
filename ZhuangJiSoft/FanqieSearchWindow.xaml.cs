using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DeskApp.Utils;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Controls;

namespace DeskApp;

/// <summary>
/// 番茄小说搜索窗（名称搜索的承载）：
/// 番茄搜索 API /api/author/search/search_book/v1 会触发字节 Bdturing 滑块验证，纯 HttpClient 返回空，
/// 故用可见 WebView2 打开搜索页，用户在页面内手动完成滑块；页面脚本带完整签名/风控头重新请求，
/// 本窗订阅 WebResourceResponseReceived 捕获该 API 的响应 JSON 并解析出结果（book_id 只在响应里，DOM 无此属性）。
/// 选中结果后返回给调用方走 ID 直达下载。
/// </summary>
public partial class FanqieSearchWindow : FluentWindow
{
    private readonly string _keyword;
    private readonly ObservableCollection<FanqieSearchResult> _results = new();
    private bool _captured;
    private bool _closing;

    /// <summary>用户选择要下载的结果；取消则为空。</summary>
    public FanqieSearchResult? SelectedResult { get; private set; }

    /// <summary>点「下载当前书」时触发，参数为书号；返回 true 表示已接受下载（宿主可提示成功）。</summary>
    public Func<string, bool>? DownloadRequested { get; set; }

    public FanqieSearchWindow(string keyword)
    {
        InitializeComponent();
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
        _keyword = keyword?.Trim() ?? string.Empty;
        KeywordBox.Text = _keyword;
        ResultList.ItemsSource = _results;
        Loaded += Window_Loaded;
        Closed += (_, _) => _closing = true;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await SearchView.EnsureCoreWebView2Async();
            SearchView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            SearchView.CoreWebView2.WebResourceResponseReceived += OnResponseReceived;
            // 番茄用 window.open 打开书页/阅读页：默认会弹新窗口，主控件仍停在搜索页，
            // 导致「下载当前书」取不到书号。这里把站内链接接管到当前控件内导航。
            SearchView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            SearchView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            NavigateSearch(_keyword);
        }
        catch (Exception ex)
        {
            StatusText.Text = "WebView2 初始化失败：" + ex.Message;
        }
    }

    /// <summary>window.open 拦截：站内链接在当前控件内打开，站外链接放行给默认处理。</summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(e.Uri) ||
                !e.Uri.Contains("fanqienovel.com", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            e.Handled = true;
            // 进入新页面后旧搜索结果面板已无意义，收起避免误导
            ResultPanel.Visibility = Visibility.Collapsed;
            SearchView.CoreWebView2.Navigate(e.Uri);
        }
        catch
        {
            // 导航失败时放行默认行为
        }
    }

    /// <summary>导航完成：更新返回按钮可用性，并按页面类型给出提示。</summary>
    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        try
        {
            if (!e.IsSuccess)
            {
                return;
            }

            BackBtn.IsEnabled = SearchView.CoreWebView2.CanGoBack;

            var url = SearchView.Source?.ToString() ?? string.Empty;
            if (!string.IsNullOrEmpty(FanqieClient.ParseBookId(url)))
            {
                StatusText.Text = "已进入小说详情页：点右上角「下载当前书」即可下载；想换一本书就点「返回」。";
            }
            else if (url.Contains("/reader/", StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = "已进入阅读页：点右上角「下载当前书」，会自动识别所属书籍并下载全本。";
            }
            else if (url.Contains("/search/", StringComparison.OrdinalIgnoreCase) && _results.Count > 0)
            {
                ResultPanel.Visibility = Visibility.Visible;
                StatusText.Text = $"已返回搜索结果（{_results.Count} 本）：选一本点下载，或点进书页后点「下载当前书」。";
            }
        }
        catch
        {
            // 状态提示失败不影响功能
        }
    }

    private void NavigateSearch(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            StatusText.Text = "请输入书名关键词再搜索。";
            return;
        }

        _captured = false;
        _results.Clear();
        ResultPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = "正在加载搜索页…若出现滑块验证，请在页面内手动完成；点进一本书的页面后，可点右上角「下载当前书」。";
        // 带时间戳参数，避免重复搜索时命中 WebView2 页面缓存而不重新请求
        var url = FanqieClient.SearchPrefix + Uri.EscapeDataString(keyword) +
                  "?t=" + DateTimeOffset.Now.ToUnixTimeMilliseconds();
        SearchView.CoreWebView2?.Navigate(url);
    }

    /// <summary>捕获搜索 API 响应。book_id 只在响应 JSON 里，必须从网络层拿。</summary>
    private void OnResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        try
        {
            var uri = e.Request.Uri;
            if (string.IsNullOrEmpty(uri) || !uri.Contains("search_book/v1", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var status = e.Response.StatusCode;
            var verify = e.Response.Headers.GetHeader("Bdturing-Verify");
            if (!string.IsNullOrEmpty(verify))
            {
                Dispatcher.Invoke(() =>
                    StatusText.Text = "检测到滑块验证：请在下方页面内完成验证；完成后点「重新搜索」刷新结果。");
                return;
            }

            if (status != 200)
            {
                return;
            }

            _ = ReadBodyAsync(e.Response);
        }
        catch
        {
            // 单个响应捕获失败不影响页面继续加载
        }
    }

    private async Task ReadBodyAsync(CoreWebView2WebResourceResponseView response)
    {
        try
        {
            await using var stream = await response.GetContentAsync();
            if (stream is null)
            {
                return;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var json = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(json) || _captured)
            {
                return;
            }

            var results = FanqieClient.ParseSearchResponse(json);
            if (results.Count == 0)
            {
                return;
            }

            _captured = true;
            if (_closing)
            {
                return;
            }

            Dispatcher.Invoke(() =>
            {
                _results.Clear();
                foreach (var item in results)
                {
                    _results.Add(item);
                }

                ResultCountText.Text = $"搜索结果（{results.Count} 本，双击或选后点下载）";
                ResultPanel.Visibility = Visibility.Visible;
                StatusText.Text = $"已找到 {results.Count} 本小说。选中一本点「下载选中」；也可点进任意书页后点「下载当前书」。";
            });
        }
        catch
        {
            // 响应体读取失败（流已消费等）时忽略，用户可点「重新搜索」再试
        }
    }

    private void Search_Click(object sender, RoutedEventArgs e) => NavigateSearch(KeywordBox.Text);

    /// <summary>「下载当前书」：从当前页面取书号并提交下载；不关闭本窗口，可继续浏览/连续下载。</summary>
    private async void DownloadCurrent_Click(object sender, RoutedEventArgs e)
    {
        var bookId = await ResolveCurrentBookIdAsync();
        if (string.IsNullOrEmpty(bookId))
        {
            PromptDialog.Show(this,
                "当前页面不是小说详情页。\n请先搜索并点进一本小说（/page/… 页面），再点「下载当前书」；也可在下方结果列表里选一本点下载。",
                "提示", System.Windows.MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var accepted = DownloadRequested?.Invoke(bookId) ?? false;
        StatusText.Text = accepted
            ? $"已开始下载（书号 {bookId}）。本窗口可继续浏览，关闭后可在主界面查看进度。"
            : "已有下载任务在进行中，请等它完成后再点；也可关闭本窗口查看进度。";
    }

    /// <summary>返回上一个页面（书页 → 搜索页等）。</summary>
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (SearchView.CoreWebView2?.CanGoBack == true)
            {
                SearchView.CoreWebView2.GoBack();
            }
        }
        catch
        {
            // 无历史可回退时忽略
        }
    }

    /// <summary>
    /// 解析当前页面所属书籍：
    /// ① 优先从 URL 的 /page/{bookId} 提取；
    /// ② URL 不含书号时（如已进入 /reader/{itemId} 阅读页），读页面 __INITIAL_STATE__ 里的 bookId 兜底。
    /// </summary>
    private async Task<string?> ResolveCurrentBookIdAsync()
    {
        try
        {
            var url = SearchView.Source?.ToString() ?? string.Empty;

            // ① 详情页 URL 直接带书号
            var fromUrl = FanqieClient.ParseBookId(url);
            if (!string.IsNullOrEmpty(fromUrl))
            {
                return fromUrl;
            }

            // ② JS 兜底：详情页取 page.bookId，阅读页取 reader.chapterData.bookId
            const string script = @"(function(){
                try {
                    var s = window.__INITIAL_STATE__;
                    if (!s) { return ''; }
                    if (s.page && s.page.bookId) { return String(s.page.bookId); }
                    if (s.reader && s.reader.chapterData && s.reader.chapterData.bookId) {
                        return String(s.reader.chapterData.bookId);
                    }
                    return '';
                } catch (e) { return ''; }
            })()";
            var raw = await SearchView.ExecuteScriptAsync(script);
            var value = raw?.Trim('"');
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private void KeywordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateSearch(KeywordBox.Text);
        }
    }

    private void ResultList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultList.SelectedItem is FanqieSearchResult result)
        {
            SelectedResult = result;
            DialogResult = true;
            Close();
        }
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if (ResultList.SelectedItem is not FanqieSearchResult result)
        {
            PromptDialog.Show(this, "请先在结果列表里选中一本小说。", "提示",
                System.Windows.MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedResult = result;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        SelectedResult = null;
        DialogResult = false;
        Close();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
}
