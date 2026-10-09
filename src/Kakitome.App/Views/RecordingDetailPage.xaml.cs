using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Kakitome.Presentation.Services;
using Kakitome.Presentation.ViewModels;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Kakitome.App.Views;

#pragma warning disable CA1001 // _player is disposed in OnNavigatedFrom (pages are not IDisposable).
public sealed partial class RecordingDetailPage : Page
#pragma warning restore CA1001
{
    private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly MediaPlayer _player = new();
    private readonly List<(Run Run, SegmentViewModel Segment)> _runs = [];
    private readonly List<(Run Run, SummaryLine Line)> _summaryRuns = [];

    /// <summary>Character index of each segment's text in the whole transcript (what TextHighlighter ranges count).</summary>
    private readonly Dictionary<Run, int> _charIndex = [];
    /// <summary>
    /// Characters a paragraph break counts for in TextHighlighter ranges: none (measured — with one, the highlight drifted
    /// one character per preceding paragraph).
    /// </summary>
    private const int ParagraphBreakLength = 0;

    private TextHighlighter? _highlighter;
    private string? _loadedAudio;
    private SegmentViewModel? _lastTapped;

    public RecordingDetailPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<RecordingDetailViewModel>();
        InitializeComponent();
        Player.SetMediaPlayer(_player);
        SelectView(App.Current.Services.GetRequiredService<Kakitome.Application.Settings.ISettingsStore>().Current.General.DetailLayout);
        TranscriptText.AddHandler(TappedEvent, new TappedEventHandler(OnTranscriptTapped), handledEventsToo: true);
        TranscriptText.AddHandler(DoubleTappedEvent, new DoubleTappedEventHandler(OnTranscriptDoubleTapped), handledEventsToo: true);
        SummaryText.AddHandler(TappedEvent, new TappedEventHandler(OnSummaryTapped), handledEventsToo: true);
        ViewModel.SummarySections.CollectionChanged += (_, _) => BuildSummary();
        ViewModel.SeekRequested += OnSeekRequested;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RecordingDetailViewModel.AudioPath))
            {
                LoadAudio();
            }
            else if (e.PropertyName == nameof(RecordingDetailViewModel.ReadingBlocks))
            {
                BuildTranscript();
            }
            else if (e.PropertyName == nameof(RecordingDetailViewModel.SummaryOverview))
            {
                BuildSummary();
            }
            else if (e.PropertyName == nameof(RecordingDetailViewModel.CurrentSegment))
            {
                HighlightCurrent(scroll: true);
            }
        };
        _positionTimer.Tick += (_, _) =>
        {
            var session = _player.PlaybackSession;
            if (session.PlaybackState == MediaPlaybackState.Playing)
            {
                ViewModel.UpdatePosition(session.Position.TotalSeconds);
            }

            PositionText.Text = Kakitome.Domain.Rendering.TimeFormat.Playback(session.Position, session.NaturalDuration);
        };
    }

    public RecordingDetailViewModel ViewModel { get; }

#pragma warning disable CA1822 // x:Bind function bindings must be instance members.
    public Microsoft.UI.Xaml.Visibility VisibleWhenFalse(bool value) => value ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public Visibility HasAudio(string? path) => path is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility NoAudio(string? path) => path is null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HasItems(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public InfoBarSeverity SeverityOf(bool isError) => isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
#pragma warning restore CA1822

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _positionTimer.Start();
        if (e.Parameter is RecordingNavigation navigation)
        {
            await ViewModel.LoadAsync(navigation);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _positionTimer.Stop();
        _player.Pause();

        // Detach from the element before disposing: disposing a player the element still renders crashes natively.
        Player.SetMediaPlayer(null);
        _player.Source = null;
        _player.Dispose();
        base.OnNavigatedFrom(e);
    }

    private void LoadAudio()
    {
        if (ViewModel.AudioPath is { } path && path != _loadedAudio)
        {
            _loadedAudio = path;
            _player.Source = MediaSource.CreateFromUri(new Uri(path));
        }
    }

    private void OnSeekRequested(object? sender, double seconds)
    {
        LoadAudio();
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(seconds);
        ViewModel.UpdatePosition(seconds);
        _player.Play();
    }

    private void OnAcceptSuggestion(object sender, RoutedEventArgs e) =>
        ViewModel.AcceptSuggestionCommand.Execute((sender as FrameworkElement)?.Tag);

    /// <summary>
    /// Renders the transcript for reading (<see cref="Kakitome.Domain.Rendering.ReadingLayout"/>): short paragraphs without
    /// time stamps, the speaker named when it changes, each segment a run of the text so the segment being heard can be
    /// highlighted and clicked.
    /// </summary>
    private void BuildTranscript()
    {
        TranscriptText.Blocks.Clear();
        _runs.Clear();
        _charIndex.Clear();
        var chars = 0;
        string? lastSpeaker = null;
        foreach (var reading in ViewModel.ReadingBlocks)
        {
            var block = new Paragraph { Margin = new Thickness(0, 0, 0, 14) };
            if (ViewModel.ShowSpeakers && reading.Speaker is { Length: > 0 } speaker && speaker != lastSpeaker)
            {
                var name = new Run { Text = speaker + (ViewModel.SegmentSeparator.Length == 0 ? "：" : ": "), FontWeight = FontWeights.SemiBold };
                block.Inlines.Add(name);
                chars += name.Text.Length;
            }

            lastSpeaker = reading.Speaker ?? lastSpeaker;
            for (var i = 0; i < reading.Pieces.Count; i++)
            {
                if (i > 0 && ViewModel.SegmentSeparator.Length > 0)
                {
                    block.Inlines.Add(new Run { Text = ViewModel.SegmentSeparator });
                    chars += ViewModel.SegmentSeparator.Length;
                }

                var piece = reading.Pieces[i];
                var run = new Run { Text = piece.Text };
                block.Inlines.Add(run);
                _charIndex[run] = chars;
                chars += run.Text.Length;
                _runs.Add((run, piece.Segment));
            }

            TranscriptText.Blocks.Add(block);
            chars += ParagraphBreakLength;
        }

        HighlightCurrent(scroll: false);
    }

    /// <summary>
    /// Renders the summary like the transcript: plain paragraphs, section headings, "・" items. Items that cite a moment
    /// of the recording play from there when clicked (no time stamps shown).
    /// </summary>
    private void BuildSummary()
    {
        SummaryText.Blocks.Clear();
        _summaryRuns.Clear();
        if (ViewModel.SummaryOverview is { Length: > 0 } overview)
        {
            var block = new Paragraph { Margin = new Thickness(0, 0, 0, 14) };
            block.Inlines.Add(new Run { Text = overview });
            SummaryText.Blocks.Add(block);
        }

        foreach (var section in ViewModel.SummarySections)
        {
            var heading = new Paragraph { Margin = new Thickness(0, 10, 0, 4) };
            heading.Inlines.Add(new Run { Text = section.Heading, FontWeight = FontWeights.SemiBold });
            SummaryText.Blocks.Add(heading);
            foreach (var line in section.Lines)
            {
                var item = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
                item.Inlines.Add(new Run { Text = "・" });
                var run = new Run { Text = line.Text };
                item.Inlines.Add(run);
                SummaryText.Blocks.Add(item);
                if (line.HasTime)
                {
                    _summaryRuns.Add((run, line));
                }
            }
        }
    }

    /// <summary>A click on a summary item that cites the recording plays from that moment.</summary>
    private void OnSummaryTapped(object sender, TappedRoutedEventArgs e)
    {
        if (SummaryText.SelectedText.Length > 0)
        {
            return;
        }

        var position = SummaryText.GetPositionFromPoint(e.GetPosition(SummaryText));
        if (position is null)
        {
            return;
        }

        foreach (var (run, line) in _summaryRuns)
        {
            if (position.Offset >= run.ContentStart.Offset && position.Offset <= run.ContentEnd.Offset)
            {
                ViewModel.SeekCommand.Execute(line);
                return;
            }
        }
    }

    /// <summary>Highlights the segment being heard and, while playing, keeps it in view.</summary>
    private void HighlightCurrent(bool scroll)
    {
        // A new highlighter each time: changing the ranges of one already attached does not repaint.
        if (_highlighter is not null)
        {
            TranscriptText.TextHighlighters.Remove(_highlighter);
            _highlighter = null;
        }

        var current = ViewModel.CurrentSegment;
        var run = _runs.FirstOrDefault(r => r.Segment == current).Run;
        if (current is null || run is null)
        {
            return;
        }

        var accent = (Windows.UI.Color)Microsoft.UI.Xaml.Application.Current.Resources["SystemAccentColor"];
        _highlighter = new TextHighlighter
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x59, accent.R, accent.G, accent.B)),
            Foreground = (SolidColorBrush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorPrimaryBrush"],
        };
        _highlighter.Ranges.Add(new TextRange { StartIndex = _charIndex[run], Length = run.Text.Length });
        TranscriptText.TextHighlighters.Add(_highlighter);
        if (!scroll || TranscriptPane.Visibility != Visibility.Visible)
        {
            return;
        }

        // Bring the segment into view (about a third from the top) when it is outside the visible area.
        var rect = run.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        var top = TranscriptText.TransformToVisual(TranscriptScroller).TransformPoint(new Windows.Foundation.Point(0, rect.Top)).Y;
        if (top < 0 || top > TranscriptScroller.ViewportHeight - 48)
        {
            var target = TranscriptScroller.VerticalOffset + top - (TranscriptScroller.ViewportHeight / 3);
            TranscriptScroller.ChangeView(null, Math.Max(0, target), null);
        }
    }

    /// <summary>The segment under a point of the transcript text.</summary>
    private SegmentViewModel? SegmentAt(Windows.Foundation.Point point)
    {
        var position = TranscriptText.GetPositionFromPoint(point);
        if (position is null)
        {
            return null;
        }

        var offset = position.Offset;
        foreach (var (run, segment) in _runs)
        {
            if (offset >= run.ContentStart.Offset && offset <= run.ContentEnd.Offset)
            {
                return segment;
            }
        }

        return null;
    }

    /// <summary>A click (not a text selection) plays from the clicked segment.</summary>
    private void OnTranscriptTapped(object sender, TappedRoutedEventArgs e)
    {
        if (TranscriptText.SelectedText.Length > 0)
        {
            return;
        }

        if (SegmentAt(e.GetPosition(TranscriptText)) is { } segment)
        {
            _lastTapped = segment;
            ViewModel.SeekCommand.Execute(segment);
        }
    }

    private void OnTranscriptDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (SegmentAt(e.GetPosition(TranscriptText)) is { } segment)
        {
            _ = EditSegmentAsync(segment);
        }
    }

    /// <summary>F2 corrects the segment being played (or the one last clicked): the keyboard equivalent of double-click.</summary>
    private void OnTranscriptKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.F2 && (ViewModel.CurrentSegment ?? _lastTapped ?? ViewModel.Segments.FirstOrDefault()) is { } segment)
        {
            e.Handled = true;
            _ = EditSegmentAsync(segment);
        }
    }

    /// <summary>Shows the transcript or the summary (one at a time, ADR-032) and remembers the choice.</summary>
    private void OnViewTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var view = (sender.SelectedItem as SelectorBarItem)?.Tag as string ?? "transcript";
        TranscriptPane.Visibility = view == "transcript" ? Visibility.Visible : Visibility.Collapsed;
        SummaryPane.Visibility = view == "summary" ? Visibility.Visible : Visibility.Collapsed;
        var settings = App.Current.Services.GetRequiredService<Kakitome.Application.Settings.ISettingsStore>();
        if (settings.Current.General.DetailLayout != view)
        {
            _ = settings.UpdateAsync(s => s.General.DetailLayout = view);
        }
    }

    /// <summary>Selects the remembered view; layouts saved by older versions map to the pane they showed first.</summary>
    private void SelectView(string? saved) =>
        ViewTabs.SelectedItem = saved is "summary" or "summaryFirst" ? SummaryTab : TranscriptTab;

    /// <summary>Inline correction: a dialog with the line's text; saving marks the segment as user-edited.</summary>
    private async Task EditSegmentAsync(SegmentViewModel segment)
    {
        var box = new TextBox { Text = segment.Text, AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, MinWidth = 420 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = segment.TimeText,
            Content = box,
            PrimaryButtonText = Services.Strings.Get("Detail_Accept.Content"),
            CloseButtonText = Services.Strings.Get("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.SaveSegmentEditAsync(segment, box.Text);
        }
    }
}
