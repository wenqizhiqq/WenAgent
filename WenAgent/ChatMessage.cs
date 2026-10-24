using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace WenAgent;

public enum MessageKind
{
    User,
    Assistant,
    Streaming,
    Step,
    Tool,
    Error,
    Info
}

public sealed class ChatMessage : INotifyPropertyChanged
{
    private string _header = "";
    private string _content = "";
    private string? _fullContent;
    private ImageSource? _annotatedImage;
    private MessageKind _kind;

    public string Header
    {
        get => _header;
        set
        {
            if (_header == value) return;
            _header = value;
            OnPropertyChanged();
        }
    }

    public string Content
    {
        get => _content;
        set
        {
            if (_content == value) return;
            _content = value;
            OnPropertyChanged();
        }
    }

    /// <summary>完整内容（工具结果在气泡里会截断显示，悬浮可看全文）。</summary>
    public string? FullContent
    {
        get => _fullContent;
        set
        {
            if (_fullContent == value) return;
            _fullContent = value;
            OnPropertyChanged();
        }
    }

    public MessageKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value) return;
            _kind = value;
            OnPropertyChanged();
        }
    }

    public ImageSource? AnnotatedImage
    {
        get => _annotatedImage;
        set
        {
            if (ReferenceEquals(_annotatedImage, value)) return;
            _annotatedImage = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
