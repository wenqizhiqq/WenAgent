using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WenAgent.Agent;

namespace WenAgent;

public partial class MainWindow : Window
{
    private enum StatusKind
    {
        Idle,
        Busy,
        Ready,
        Error
    }

    private static readonly Brush IdleBrush = MakeBrush(0x94, 0xA3, 0xB8);
    private static readonly Brush BusyBrush = MakeBrush(0xD9, 0x77, 0x06);
    private static readonly Brush ReadyBrush = MakeBrush(0x16, 0xA3, 0x4A);
    private static readonly Brush ErrorBrush = MakeBrush(0xDC, 0x26, 0x26);

    private readonly ObservableCollection<ChatMessage> _messages = [];

    private AgentEngine? _engine;
    private CancellationTokenSource? _runCts;
    private ChatMessage? _stream;
    private bool _ready;
    private bool _running;
    private bool _loading;
    private bool _downloadOpened;
    private bool _scrollPending;

    public ObservableCollection<ChatMessage> Messages => _messages;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        Loaded += OnLoaded;
        Closing += OnClosing;

        AddInfo("正在查找本地模型…");
        UpdateUiState();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await LocateAndLoadAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _runCts?.Cancel();
    }

    private async Task LocateAndLoadAsync()
    {
        if (_loading || _running) return;

        _loading = true;
        _ready = false;
        MissingPanel.Visibility = Visibility.Collapsed;
        SetStatus("正在查找本地模型…", StatusKind.Busy);
        UpdateUiState();

        try
        {
            var progress = new Progress<string>(s => SetStatus(s, StatusKind.Busy));
            string? path = await ModelLocator.LocateAsync(progress, CancellationToken.None);

            if (path == null)
            {
                ShowMissing("未找到 GGUF 模型",
                    ModelLocator.SearchHint, openDownload: true);
                return;
            }

            await LoadModelInternalAsync(path);
        }
        catch (Exception ex)
        {
            ShowMissing("模型加载失败",
                ex.Message + "\n\n" + ModelLocator.SearchHint, openDownload: false);
        }
        finally
        {
            _loading = false;
            UpdateUiState();
        }
    }

    private async Task<bool> LoadModelInternalAsync(string path)
    {
        MissingPanel.Visibility = Visibility.Collapsed;
        SetStatus($"正在加载 {Path.GetFileName(path)} …", StatusKind.Busy);

        _engine?.Dispose();
        _engine = null;
        _ready = false;
        UpdateUiState();

        var engine = new AgentEngine(path, UserSettings.LoadWorkspace())
        {
            AllowCommands = CommandCheck.IsChecked == true
        };

        try
        {
            var (available, _) = SystemMemory.Get();
            if (available > 0 && available < 5.5)
                AddInfo($"提示：当前可用物理内存约 {available:F1} GB，7B 模型加载约需 5.5GB。" +
                        "如果加载失败，请关闭其他程序后重试。");

            var progress = new Progress<string>(s => SetStatus(s, StatusKind.Busy));
            await engine.LoadAsync(progress, CancellationToken.None);

            _engine = engine;
            _ready = true;
            ModelLocator.SavePath(path);

            SetStatus($"就绪 · {Path.GetFileName(path)}", StatusKind.Ready);
            StatusText.ToolTip = $"模型：{path}\n工作目录：{engine.WorkspacePath}";
            WorkingDirButton.ToolTip = engine.WorkspacePath;

            AddInfo("模型已就绪。");
            AddInfo($"模型：{path}");
            AddInfo($"工作目录：{engine.WorkspacePath}");
            AddInfo("可以试试：看看工作目录里有什么；或者：写一个 hello.txt 再读出来。");
            AddInfo("要修改已有文件：点右上角“工作目录”选择文件所在文件夹，或直接在任务里写绝对路径。");
            return true;
        }
        catch (Exception ex)
        {
            engine.Dispose();
            _ready = false;
            LlamaLog.SaveToFile();
            ShowMissing("模型加载失败", DescribeLoadFailure(ex), openDownload: false);
            return false;
        }
        finally
        {
            UpdateUiState();
        }
    }

    private static string DescribeLoadFailure(Exception ex)
    {
        string native = LlamaLog.Summary();
        bool memoryShortage = native.Contains("unable to allocate", StringComparison.OrdinalIgnoreCase)
                              || native.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase)
                              || native.Contains("out of memory", StringComparison.OrdinalIgnoreCase);

        string detail;
        if (memoryShortage)
        {
            var (available, total) = SystemMemory.Get();
            detail = "内存不足，模型加载失败。\n" +
                     $"当前可用物理内存约 {available:F1} GB（共 {total:F1} GB），" +
                     "7B Q4 模型加载约需 5.5GB。\n" +
                     "请关闭其他程序（尤其是其他 WenAgent 实例或大型项目）后点“重新搜索”，" +
                     "或改用更小的 4B 模型。";
        }
        else
        {
            detail = ex.Message;
        }

        if (native.Length > 0)
            detail += "\n\n原生日志：\n" + native;

        detail += "\n\n详细日志：%LOCALAPPDATA%\\WenAgent\\last-load.log";
        return detail;
    }

    private void ShowMissing(string title, string detail, bool openDownload)
    {
        MissingTitle.Text = title;
        MissingDetail.Text = detail;
        MissingPanel.Visibility = Visibility.Visible;
        SetStatus(title, StatusKind.Error);
        _ready = false;

        if (openDownload && !_downloadOpened)
        {
            _downloadOpened = true;
            if (ModelLocator.OpenDownloadPage())
                AddInfo("未找到模型，已在浏览器打开 ModelScope 下载页面。下载后放到 D:\\wqz\\models，再点“重新搜索”。");
        }

        UpdateUiState();
    }

    private async Task SendAsync()
    {
        if (!_ready || _running || _loading || _engine == null) return;

        string task = InputBox.Text.Trim();
        if (task.Length == 0) return;

        InputBox.Clear();
        AddMessage(new ChatMessage { Kind = MessageKind.User, Header = "你", Content = task });

        _running = true;
        _stream = null;
        _runCts = new CancellationTokenSource();
        SetStatus("第 1 步 · 正在生成…", StatusKind.Busy);
        UpdateUiState();

        AgentEngine engine = _engine;
        var sink = new Progress<AgentEvent>(HandleAgentEvent);

        try
        {
            await Task.Run(() => engine.RunAsync(task, sink, _runCts.Token), _runCts.Token);
        }
        catch (OperationCanceledException)
        {
            FinishStream("", MessageKind.Info, "已中断");
            AddInfo("任务已中断。");
            SetStatus("已中断", StatusKind.Idle);
        }
        catch (Exception ex)
        {
            FinishStream("错误", MessageKind.Error, ex.Message);
            AddInfo("本次任务出错，对话上下文已重置。");
            SetStatus("执行出错", StatusKind.Error);
        }
        finally
        {
            _running = false;
            _runCts?.Dispose();
            _runCts = null;
            _stream = null;
            UpdateUiState();
            ScrollToEnd(force: true);
            InputBox.Focus();
        }
    }

    private void HandleAgentEvent(AgentEvent ev)
    {
        switch (ev.Kind)
        {
            case AgentEventKind.GenerationStarted:
                if (_stream != null && _stream.Kind == MessageKind.Streaming)
                {
                    string previous = _stream.Header.Replace(" · 正在生成", "");
                    _stream.Kind = MessageKind.Info;
                    _stream.Header = "";
                    _stream.Content = $"{previous}的输出未能解析，正在重试…";
                }

                _stream = new ChatMessage
                {
                    Kind = MessageKind.Streaming,
                    Header = $"第 {ev.Text} 步 · 正在生成",
                    Content = ""
                };
                _messages.Add(_stream);
                SetStatus($"第 {ev.Text} 步 · 正在生成…", StatusKind.Busy);
                break;

            case AgentEventKind.Token:
                if (_stream != null) _stream.Content += ev.Text;
                break;

            case AgentEventKind.StepStarted:
                if (_stream != null)
                {
                    _stream.Kind = MessageKind.Step;
                    _stream.Header = $"第 {ev.Detail} 步";
                    _stream.Content = $"调用工具：{ev.Text}";
                }
                SetStatus($"第 {ev.Detail} 步 · 执行 {ev.Text}…", StatusKind.Busy);
                break;

            case AgentEventKind.ToolResult:
                _messages.Add(new ChatMessage
                {
                    Kind = ev.Success ? MessageKind.Tool : MessageKind.Error,
                    Header = $"工具结果 · {ev.Detail}",
                    Content = Truncate(ev.Text, 2000),
                    FullContent = ev.Text
                });
                break;

            case AgentEventKind.Final:
                FinishStream("WenAgent", MessageKind.Assistant, ev.Text);
                SetStatus("就绪", StatusKind.Ready);
                break;

            case AgentEventKind.Info:
                AddInfo(ev.Text);
                break;
        }

        ScrollToEnd();
    }

    private void FinishStream(string header, MessageKind kind, string content)
    {
        if (_stream == null || _stream.Kind != MessageKind.Streaming)
        {
            AddMessage(new ChatMessage { Kind = kind, Header = header, Content = content });
            return;
        }

        _stream.Kind = kind;
        _stream.Header = header;
        _stream.Content = content;
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _runCts?.Cancel();
        StopButton.IsEnabled = false;
        SetStatus("正在中断…", StatusKind.Busy);
    }

    private async void NewChat_Click(object sender, RoutedEventArgs e)
    {
        if (_engine == null || _running || _loading) return;

        NewChatButton.IsEnabled = false;
        try
        {
            AgentEngine engine = _engine;
            await Task.Run(engine.NewConversation);
            _messages.Clear();
            _stream = null;
            AddInfo("新的对话已开始。");
            AddInfo($"工作目录：{engine.WorkspacePath}");
        }
        catch (Exception ex)
        {
            AddInfo("重置对话失败：" + ex.Message);
        }
        finally
        {
            UpdateUiState();
        }
    }

    private async void WorkingDir_Click(object sender, RoutedEventArgs e)
    {
        if (_engine == null || _running || _loading) return;

        var dialog = new OpenFolderDialog
        {
            Title = "选择 Agent 的工作目录",
            InitialDirectory = _engine.WorkspacePath
        };

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        string path = dialog.FolderName;
        _loading = true;
        UpdateUiState();

        try
        {
            AgentEngine engine = _engine;
            await Task.Run(() => engine.SetWorkspace(path));

            UserSettings.SaveWorkspace(path);
            WorkingDirButton.ToolTip = path;
            StatusText.ToolTip = $"模型：{engine.ModelPath}\n工作目录：{path}";

            AddInfo($"工作目录已切换到：{path}");
            AddInfo("模型上下文已重置；下一轮任务会基于新目录执行，可以直接说“修改 xxx.xlsx”。");
        }
        catch (Exception ex)
        {
            AddInfo("切换工作目录失败：" + ex.Message);
        }
        finally
        {
            _loading = false;
            UpdateUiState();
        }
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            if (SendButton.IsEnabled) _ = SendAsync();
        }
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        InputPlaceholder.Visibility = InputBox.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateUiState();
    }

    private void CommandCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_engine != null) _engine.AllowCommands = CommandCheck.IsChecked == true;
    }

    private void OpenDownload_Click(object sender, RoutedEventArgs e)
    {
        if (ModelLocator.OpenDownloadPage())
            AddInfo("已在浏览器打开 ModelScope 下载页面。");
    }

    private async void ChooseModel_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _running) return;

        var dialog = new OpenFileDialog
        {
            Title = "选择 GGUF 模型",
            Filter = "GGUF 模型 (*.gguf)|*.gguf|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true) return;

        ModelLocator.SavePath(dialog.FileName);
        _loading = true;
        UpdateUiState();
        try
        {
            await LoadModelInternalAsync(dialog.FileName);
        }
        finally
        {
            _loading = false;
            UpdateUiState();
        }
    }

    private async void Research_Click(object sender, RoutedEventArgs e)
    {
        await LocateAndLoadAsync();
    }

    private void UpdateUiState()
    {
        bool idle = !_running && !_loading;
        SendButton.IsEnabled = _ready && idle && !string.IsNullOrWhiteSpace(InputBox.Text);
        StopButton.IsEnabled = _running;
        InputBox.IsEnabled = _ready && !_loading;
        NewChatButton.IsEnabled = _ready && idle;
        WorkingDirButton.IsEnabled = _ready && idle;
        CommandCheck.IsEnabled = _engine != null && idle;
        MissingPanel.IsEnabled = idle;
    }

    private void AddMessage(ChatMessage message)
    {
        _messages.Add(message);
        ScrollToEnd(force: true);
    }

    private void AddInfo(string text)
    {
        AddMessage(new ChatMessage { Kind = MessageKind.Info, Content = text });
    }

    private void ScrollToEnd(bool force = false)
    {
        if (!force && ChatScroll.VerticalOffset < ChatScroll.ScrollableHeight - 180) return;
        if (_scrollPending) return;

        _scrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _scrollPending = false;
            ChatScroll.ScrollToEnd();
        }));
    }

    private void SetStatus(string text, StatusKind kind)
    {
        StatusText.Text = text;
        StatusText.ToolTip = text.Length > 48 ? text : null;
        StatusDot.Fill = kind switch
        {
            StatusKind.Ready => ReadyBrush,
            StatusKind.Busy => BusyBrush,
            StatusKind.Error => ErrorBrush,
            _ => IdleBrush
        };
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "\n…（完整内容可通过悬浮气泡查看）";

    private static SolidColorBrush MakeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
