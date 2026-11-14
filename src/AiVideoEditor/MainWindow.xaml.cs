using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AiVideoEditor;

public partial class MainWindow : Window
{
    private Project _project = new();
    private string? _projectPath;
    private TimelineClip? _selected;
    private readonly Updater _updater = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _loadingDetails;
    private double _durationSeconds;

    public MainWindow()
    {
        InitializeComponent();

        newButton.Click += (_, _) => NewProject();
        openButton.Click += (_, _) => OpenProject();
        saveButton.Click += (_, _) => SaveProject();
        saveAsButton.Click += (_, _) => SaveProjectAs();
        exportButton.Click += (_, _) => ExportFfmpeg();
        projectNameText.TextChanged += (_, _) =>
        {
            if (!_loadingDetails) { _project.Name = projectNameText.Text; MarkDirty(); }
        };
        globalPromptText.TextChanged += (_, _) =>
        {
            if (!_loadingDetails) { _project.GlobalPrompt = globalPromptText.Text; MarkDirty(); }
        };

        addVideosButton.Click += (_, _) => AddVideos();
        moveUpButton.Click += (_, _) => MoveSelected(-1);
        moveDownButton.Click += (_, _) => MoveSelected(1);
        removeButton.Click += (_, _) => RemoveSelected();
        splitButton.Click += (_, _) => SplitAtPlayhead();
        timelineList.SelectionChanged += (_, _) => LoadSelected();

        playPauseButton.Click += (_, _) => TogglePlay();
        setInButton.Click += (_, _) => SetTrim(true);
        setOutButton.Click += (_, _) => SetTrim(false);
        positionSlider.ValueChanged += (_, _) =>
        {
            // Seek only while the user is dragging the thumb.
            if (System.Windows.Input.Mouse.LeftButton ==
                System.Windows.Input.MouseButtonState.Pressed)
                preview.Position = TimeSpan.FromSeconds(positionSlider.Value);
        };
        preview.MediaOpened += (_, _) =>
        {
            _durationSeconds = preview.NaturalDuration.HasTimeSpan
                ? preview.NaturalDuration.TimeSpan.TotalSeconds : 0;
            positionSlider.Maximum = Math.Max(_durationSeconds, 1);
            if (_selected is not null && _selected.InSeconds > 0)
                preview.Position = TimeSpan.FromSeconds(_selected.InSeconds);
            UpdateTimeLabel();
        };

        inText.LostFocus += (_, _) => ApplyTrimText();
        outText.LostFocus += (_, _) => ApplyTrimText();
        promptText.TextChanged += (_, _) =>
        {
            if (!_loadingDetails && _selected is not null)
            {
                _selected.Prompt = promptText.Text;
                MarkDirty();
                timelineList.Items.Refresh();
            }
        };

        checkUpdateButton.Click += async (_, _) =>
        {
            statusText.Text = "Checking for updates...";
            await _updater.CheckAsync();
            if (restartUpdateButton.Visibility != Visibility.Visible)
                statusText.Text = "Up to date.";
        };
        restartUpdateButton.Click += (_, _) => _updater.ApplyAndRestart();
        _updater.StatusChanged += s => Dispatcher.Invoke(() => statusText.Text = s);
        _updater.UpdateReady += () => Dispatcher.Invoke(() =>
            restartUpdateButton.Visibility = Visibility.Visible);

        _timer.Tick += (_, _) =>
        {
            if (preview.Source is null) return;
            positionSlider.Value = preview.Position.TotalSeconds;
            UpdateTimeLabel();
        };
        _timer.Start();

        RefreshAll();
        _ = _updater.CheckAsync(); // silent when up to date / offline
    }

    // --- Project ---

    private void NewProject()
    {
        _project = new Project();
        _projectPath = null;
        _selected = null;
        RefreshAll();
        statusText.Text = "New project.";
    }

    private void OpenProject()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "AI Video Editor project (*.aive.json)|*.aive.json",
            InitialDirectory = ProjectStore.ProjectsDir,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _project = ProjectStore.Load(dlg.FileName);
            _projectPath = dlg.FileName;
            _selected = null;
            RefreshAll();
            statusText.Text = $"Opened {dlg.FileName}.";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Open failed"); }
    }

    private void SaveProject()
    {
        if (_projectPath is null) { SaveProjectAs(); return; }
        ProjectStore.Save(_project, _projectPath);
        Title = "AI Video Editor";
        statusText.Text = $"Saved {_projectPath}.";
    }

    private void SaveProjectAs()
    {
        Directory.CreateDirectory(ProjectStore.ProjectsDir);
        var dlg = new SaveFileDialog
        {
            Filter = "AI Video Editor project (*.aive.json)|*.aive.json",
            InitialDirectory = ProjectStore.ProjectsDir,
            FileName = (_project.Name.Length > 0 ? _project.Name : "Untitled") + ".aive.json",
        };
        if (dlg.ShowDialog() != true) return;
        _projectPath = dlg.FileName;
        SaveProject();
    }

    private void ExportFfmpeg()
    {
        if (_projectPath is null) { SaveProjectAs(); if (_projectPath is null) return; }
        else ProjectStore.Save(_project, _projectPath);
        var bat = ProjectStore.ExportFfmpeg(_project, _projectPath);
        statusText.Text = $"Exported {bat}. Run it to render trims (ffmpeg -c copy).";
        MessageBox.Show($"Wrote {bat}.\nRun it to render the trimmed concat.\n" +
            "Note: -c copy cuts on keyframes; frame-accurate cuts need a re-encode pass (later).",
            "Exported");
    }

    // --- Timeline ---

    private void AddVideos()
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Video files (*.mp4;*.mkv;*.mov;*.avi)|*.mp4;*.mkv;*.mov;*.avi|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        foreach (var file in dlg.FileNames)
            _project.Clips.Add(new TimelineClip { SourcePath = file });
        MarkDirty();
        RefreshTimeline();
        statusText.Text = $"Added {dlg.FileNames.Length} clip(s).";
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
        _project.Clips.Remove(_selected);
        _selected = null;
        MarkDirty();
        RefreshTimeline();
        LoadSelected();
    }

    private void SplitAtPlayhead()
    {
        if (_selected is null) return;
        var at = preview.Position.TotalSeconds;
        if (at <= 0) return;
        // Split inside the clip's current trim range (or full clip).
        var end = _selected.OutSeconds > _selected.InSeconds
            ? _selected.OutSeconds : _durationSeconds;
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

    private void LoadSelected()
    {
        _selected = timelineList.SelectedItem as TimelineClip;
        _loadingDetails = true;
        try
        {
            if (_selected is null)
            {
                preview.Source = null;
                sourceText.Text = "";
                inText.Text = "0"; outText.Text = "0";
                gameText.Text = ""; groupsText.Text = "";
                noteText.Text = ""; promptText.Text = "";
                return;
            }
            if (File.Exists(_selected.SourcePath))
            {
                preview.Source = new Uri(_selected.SourcePath);
                preview.Position = TimeSpan.FromSeconds(_selected.InSeconds);
            }
            else
            {
                preview.Source = null;
                statusText.Text = $"Missing file: {_selected.SourcePath}";
            }
            sourceText.Text = _selected.SourcePath;
            inText.Text = _selected.InSeconds.ToString("0.###");
            outText.Text = _selected.OutSeconds.ToString("0.###");
            gameText.Text = _selected.Game;
            groupsText.Text = string.Join(", ", _selected.Groups);
            noteText.Text = _selected.Note;
            promptText.Text = _selected.Prompt;
            UpdateTimeLabel();
        }
        finally { _loadingDetails = false; }
    }

    private void TogglePlay()
    {
        if (preview.Source is null) return;
        // MediaElement has no IsPlaying; track via a tag: playing when the
        // button says Pause. Good enough for a v1 preview.
        if ((string)playPauseButton.Content == "Play")
        {
            preview.Play();
            playPauseButton.Content = "Pause";
        }
        else
        {
            preview.Pause();
            playPauseButton.Content = "Play";
        }
    }

    private void SetTrim(bool isIn)
    {
        if (_selected is null) return;
        var at = preview.Position.TotalSeconds;
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
            _selected.OutSeconds = 0;
        MarkDirty();
        RefreshTimeline();
    }

    private void UpdateTimeLabel()
    {
        var total = _durationSeconds > 0 ? $"{_durationSeconds:0.0}s" : "?";
        timeLabel.Text = $"{preview.Position.TotalSeconds:0.0}s / {total}";
    }

    // --- Helpers ---

    private void RefreshAll()
    {
        _loadingDetails = true;
        try
        {
            projectNameText.Text = _project.Name;
            globalPromptText.Text = _project.GlobalPrompt;
        }
        finally { _loadingDetails = false; }
        RefreshTimeline();
        LoadSelected();
    }

    private void RefreshTimeline()
    {
        timelineList.ItemsSource = null;
        timelineList.ItemsSource = _project.Clips;
    }

    private void MarkDirty()
    {
        if (!Title.EndsWith("*")) Title = "AI Video Editor *";
    }
}
