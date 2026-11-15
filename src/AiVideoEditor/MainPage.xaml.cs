// Editor page: the WPF MainWindow logic, ported to Uno/WinUI.
// MediaElement -> MediaPlayerElement, dialogs -> Storage pickers so the
// same code runs on Windows and the web build.
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
#if WINDOWS
using WinRT.Interop;
#endif

namespace AiVideoEditor;

public sealed partial class MainPage : Page
{
    private Project _project = new();
    private StorageFile? _currentFile;
    private string? _projectPath;
    private TimelineClip? _selected;
    // Files picked this session. The model only stores paths (and on the
    // web build there is no path at all), so playback needs the live handle.
    private readonly Dictionary<TimelineClip, StorageFile> _clipFiles = new();
#if WINDOWS
    private readonly Updater _updater = new();
#endif
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _loadingDetails;
    private bool _seekDragging;
    private bool _playerHandlersAttached;

    public MainPage()
    {
        this.InitializeComponent();

        openButton.Click += async (_, _) => await OpenProjectAsync();
        saveButton.Click += async (_, _) => await SaveProjectAsync();
        exportButton.Click += async (_, _) => await ExportFfmpegAsync();
        projectNameText.TextChanged += (_, _) =>
        {
            if (!_loadingDetails) { _project.Name = projectNameText.Text; MarkDirty(); }
        };

        addVideosButton.Click += async (_, _) => await AddVideosAsync();
        moveUpButton.Click += (_, _) => MoveSelected(-1);
        moveDownButton.Click += (_, _) => MoveSelected(1);
        removeButton.Click += (_, _) => RemoveSelected();
        splitButton.Click += (_, _) => SplitAtPlayhead();
        timelineList.SelectionChanged += (_, _) => LoadSelected();

        playPauseButton.Click += (_, _) => TogglePlay();
        setInButton.Click += (_, _) => SetTrim(true);
        setOutButton.Click += (_, _) => SetTrim(false);
        positionSlider.PointerPressed += (_, _) => _seekDragging = true;
        positionSlider.PointerReleased += (_, _) => _seekDragging = false;
        positionSlider.ValueChanged += (_, _) =>
        {
            if (_seekDragging && Player is not null)
                Player.PlaybackSession.Position = TimeSpan.FromSeconds(positionSlider.Value);
        };

        inText.LostFocus += (_, _) => ApplyTrimText();
        outText.LostFocus += (_, _) => ApplyTrimText();
        inText.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) ApplyTrimText();
        };
        outText.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) ApplyTrimText();
        };
        promptText.TextChanged += (_, _) =>
        {
            if (!_loadingDetails && _selected is not null)
            {
                _selected.Prompt = promptText.Text;
                MarkDirty();
            }
        };

#if WINDOWS
        checkUpdateButton.Click += async (_, _) =>
        {
            statusText.Text = "Checking for updates...";
            await _updater.CheckAsync();
            if (restartUpdateButton.Visibility != Visibility.Visible)
                statusText.Text = "Up to date.";
        };
        restartUpdateButton.Click += (_, _) => _updater.ApplyAndRestart();
        _updater.StatusChanged += s => DispatcherQueue.TryEnqueue(() => statusText.Text = s);
        _updater.UpdateReady += () => DispatcherQueue.TryEnqueue(() =>
            restartUpdateButton.Visibility = Visibility.Visible);
#else
        // Export and the self-updater are Windows-only: the web build
        // redeploys from Actions, and ffmpeg export needs real file
        // paths and a local ffmpeg.
        exportButton.Visibility = Visibility.Collapsed;
        checkUpdateButton.Visibility = Visibility.Collapsed;
#endif

        _timer.Tick += (_, _) =>
        {
            if (Player is null || previewPlayer.Source is null) return;
            if (!_seekDragging)
                positionSlider.Value = Player.PlaybackSession.Position.TotalSeconds;
            UpdateTimeLabel();
        };
        _timer.Start();

        RefreshAll();
#if WINDOWS
        _ = _updater.CheckAsync(); // silent when up to date / offline
#endif
    }

    private MediaPlayer? Player => previewPlayer.MediaPlayer;

    // --- Project ---

    private async Task OpenProjectAsync()
    {
        var picker = NewPicker();
        picker.FileTypeFilter.Add(".aive.json");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            _project = await ProjectStore.LoadFromFileAsync(file);
            _currentFile = file;
            _projectPath = file.Path;
            _selected = null;
            _clipFiles.Clear();
            RefreshAll();
            statusText.Text = $"Opened {file.Name}.";
        }
        catch (Exception ex) { statusText.Text = $"Open failed: {ex.Message}"; }
    }

    private async Task SaveProjectAsync()
    {
#if __WASM__
        // The web build can't write back to a picked file; every save is
        // a fresh download through the save picker.
        await SaveProjectAsAsync();
        return;
#else
        if (_currentFile is null) { await SaveProjectAsAsync(); return; }
        await ProjectStore.SaveToFileAsync(_project, _currentFile);
        MarkClean();
        statusText.Text = $"Saved {_currentFile.Name}.";
#endif
    }

    private async Task SaveProjectAsAsync()
    {
        var picker = new FileSavePicker();
        InitPickerWindow(picker);
        picker.FileTypeChoices.Add("AI Video Editor project", new List<string> { ".aive.json" });
        picker.SuggestedFileName =
            (_project.Name.Length > 0 ? _project.Name : "Untitled") + ".aive.json";
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        _currentFile = file;
        _projectPath = file.Path;
        await ProjectStore.SaveToFileAsync(_project, file);
        MarkClean();
        statusText.Text = $"Saved {file.Name}.";
    }

    private async Task ExportFfmpegAsync()
    {
#if WINDOWS
        if (_projectPath is null || _projectPath.Length == 0)
        {
            await SaveProjectAsAsync();
            if (_projectPath is null || _projectPath.Length == 0) return;
        }
        else if (_currentFile is not null)
        {
            await ProjectStore.SaveToFileAsync(_project, _currentFile);
        }
        var bat = ProjectStore.ExportFfmpeg(_project, _projectPath);
        statusText.Text = $"Exported {bat}. Run it to render trims (ffmpeg -c copy, keyframe cuts only).";
#else
        await Task.CompletedTask;
#endif
    }

    // --- Timeline ---

    private async Task AddVideosAsync()
    {
        var picker = NewPicker();
        picker.FileTypeFilter.Add(".mp4");
        picker.FileTypeFilter.Add(".mkv");
        picker.FileTypeFilter.Add(".mov");
        picker.FileTypeFilter.Add(".avi");
        picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;
        foreach (var file in files)
        {
            // file.Path is empty on the web build; the name still
            // identifies the clip there.
            var clip = new TimelineClip
            {
                SourcePath = string.IsNullOrEmpty(file.Path) ? file.Name : file.Path,
            };
            _project.Clips.Add(clip);
            _clipFiles[clip] = file;
        }
        MarkDirty();
        RefreshTimeline();
        statusText.Text = $"Added {files.Count} clip(s).";
    }

    private void MoveSelected(int delta)
    {
        if (_selected is null) return;
        var i = _project.Clips.IndexOf(_selected);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _project.Clips.Count) return;
        (_project.Clips[i], _project.Clips[j]) = (_project.Clips[j], _project.Clips[i]);
        MarkDirty();
        RefreshTimeline();
        timelineList.SelectedItem = _selected;
    }

    private void RemoveSelected()
    {
        if (_selected is null) return;
        _clipFiles.Remove(_selected);
        _project.Clips.Remove(_selected);
        _selected = null;
        MarkDirty();
        RefreshTimeline();
        LoadSelected();
    }

    private void SplitAtPlayhead()
    {
        if (_selected is null || Player is null) return;
        var at = Player.PlaybackSession.Position.TotalSeconds;
        if (at <= 0) return;
        // Split inside the clip's current trim range (or full clip).
        var end = _selected.OutSeconds > _selected.InSeconds
            ? _selected.OutSeconds : _selected.DurationSeconds;
        if (end <= 0 || at <= _selected.InSeconds || at >= end) return;
        var second = _selected.Clone();
        second.InSeconds = at;
        second.OutSeconds = end;
        second.Prompt = _selected.Prompt;
        _selected.OutSeconds = at;
        var i = _project.Clips.IndexOf(_selected);
        _project.Clips.Insert(i + 1, second);
        MarkDirty();
        RefreshTimeline();
        timelineList.SelectedItem = second;
        statusText.Text = $"Split at {at:0.0}s.";
    }

    private async void LoadSelected()
    {
        _selected = timelineList.SelectedItem as TimelineClip;
        _loadingDetails = true;
        try
        {
            if (_selected is null)
            {
                previewPlayer.Source = null;
                playPauseButton.Content = "Play";
                sourceText.Text = "";
                inText.Text = "0"; outText.Text = "0";
                promptText.Text = "";
                return;
            }

            StorageFile? file = null;
            if (_clipFiles.TryGetValue(_selected, out var picked))
            {
                file = picked;
            }
            else if (File.Exists(_selected.SourcePath))
            {
                file = await StorageFile.GetFileFromPathAsync(_selected.SourcePath);
            }

            if (file is not null)
            {
                previewPlayer.Source = MediaSource.CreateFromStorageFile(file);
                EnsurePlayerHandlers();
            }
            else
            {
                previewPlayer.Source = null;
                statusText.Text = $"Missing file: {_selected.SourcePath}";
            }
            sourceText.Text = _selected.SourcePath;
            inText.Text = _selected.InSeconds.ToString("0.###");
            outText.Text = _selected.OutSeconds.ToString("0.###");
            promptText.Text = _selected.Prompt;
            positionSlider.Maximum = Math.Max(_selected.DurationSeconds, 1);
            UpdateTimeLabel();
        }
        finally { _loadingDetails = false; }
    }

    private void EnsurePlayerHandlers()
    {
        if (_playerHandlersAttached || Player is null) return;
        _playerHandlersAttached = true;
        Player.MediaOpened += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_selected is null || Player is null) return;
                var duration = Player.PlaybackSession.NaturalDuration.TotalSeconds;
                if (duration > 0)
                {
                    _selected.DurationSeconds = duration;
                    positionSlider.Maximum = duration;
                }
                if (_selected.InSeconds > 0)
                    Player.PlaybackSession.Position = TimeSpan.FromSeconds(_selected.InSeconds);
                playPauseButton.Content = "Play";
                UpdateTimeLabel();
            });
        };
        Player.CurrentStateChanged += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                playPauseButton.Content = Player?.CurrentState == MediaPlayerState.Playing
                    ? "Pause" : "Play";
            });
        };
    }

    private void TogglePlay()
    {
        if (Player is null || previewPlayer.Source is null) return;
        if (Player.CurrentState == MediaPlayerState.Playing)
            Player.Pause();
        else
            Player.Play();
    }

    private void SetTrim(bool isIn)
    {
        if (_selected is null || Player is null) return;
        var at = Player.PlaybackSession.Position.TotalSeconds;
        if (isIn) _selected.InSeconds = at;
        else _selected.OutSeconds = at;
        if (_selected.OutSeconds > 0 && _selected.OutSeconds <= _selected.InSeconds)
            _selected.OutSeconds = 0; // back to "full" rather than invalid range
        MarkDirty();
        RefreshTimeline();
        LoadSelected();
    }

    private void ApplyTrimText()
    {
        if (_loadingDetails || _selected is null) return;
        if (double.TryParse(inText.Text, out var i)) _selected.InSeconds = Math.Max(0, i);
        if (double.TryParse(outText.Text, out var o)) _selected.OutSeconds = Math.Max(0, o);
        if (_selected.OutSeconds > 0 && _selected.OutSeconds <= _selected.InSeconds)
            _selected.OutSeconds = 0; // back to "full" rather than invalid range
        MarkDirty();
        RefreshTimeline();
    }

    private void UpdateTimeLabel()
    {
        var pos = Player?.PlaybackSession.Position.TotalSeconds ?? 0;
        var total = _selected is not null && _selected.DurationSeconds > 0
            ? $"{_selected.DurationSeconds:0.0}s" : "?";
        timeLabel.Text = $"{pos:0.0}s / {total}";
    }

    // --- Helpers ---

    private FileOpenPicker NewPicker()
    {
        var picker = new FileOpenPicker();
        InitPickerWindow(picker);
        return picker;
    }

    private static void InitPickerWindow(object picker)
    {
#if WINDOWS
        // Unpackaged WinAppSDK apps must give pickers a window handle.
        if (App.MainWindowInstance is not null)
        {
            var hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
            if (picker is FileOpenPicker openPicker)
                InitializeWithWindow.Initialize(openPicker, hwnd);
            else if (picker is FileSavePicker savePicker)
                InitializeWithWindow.Initialize(savePicker, hwnd);
        }
#endif
    }

    private void RefreshAll()
    {
        _loadingDetails = true;
        try
        {
            projectNameText.Text = _project.Name;
        }
        finally { _loadingDetails = false; }
        RefreshTimeline();
        LoadSelected();
    }

    private void RefreshTimeline()
    {
        timelineList.ItemsSource = null;
        timelineList.ItemsSource = _project.Clips;
        timelineList.SelectedItem = _selected;
    }

    private void MarkDirty()
    {
        if (App.MainWindowInstance is not null
            && !App.MainWindowInstance.Title.EndsWith("*"))
            App.MainWindowInstance.Title = "AI Video Editor *";
    }

    private void MarkClean()
    {
        if (App.MainWindowInstance is not null)
            App.MainWindowInstance.Title = "AI Video Editor";
    }
}
