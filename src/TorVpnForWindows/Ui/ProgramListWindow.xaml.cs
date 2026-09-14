using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TorVpnForWindows.Core;
using TorVpnForWindows.Localization;

namespace TorVpnForWindows.Ui;

/// <summary>
/// Edits one program list. Programs are added from the ones running now or by choosing an
/// executable, and every entry is kept as the exact path the file system resolves it to, so the
/// list holds precisely what the rules will match. Typing a name is deliberately not offered.
///
/// The window edits its own copy. <see cref="Changed"/> and <see cref="Entries"/> tell the caller
/// what to save once it closes, so a list is applied once rather than after every click.
/// </summary>
public partial class ProgramListWindow : Window
{
    private readonly List<string> _entries;
    private readonly ObservableCollection<ProgramRow> _listed = [];
    private readonly ObservableCollection<ProgramRow> _running = [];
    private IReadOnlyList<RunningProgram> _allRunning = [];
    private bool _loadingRunning;

    public ProgramListWindow(string title, string description, IEnumerable<string> entries)
    {
        InitializeComponent();

        _entries = entries.ToList();

        Title = title;
        TitleText.Text = title;
        DescriptionText.Text = description;
        MatchNoteText.Text = Strings.ListWindowMatchNote;
        ListedHeader.Text = Strings.ListWindowListed;
        RunningHeader.Text = Strings.ListWindowRunning;
        SearchLabel.Text = Strings.ListWindowSearch;
        RefreshButton.Content = Strings.ListWindowRefresh;
        BrowseButton.Content = Strings.ListWindowBrowse;
        CloseButton.Content = Strings.ListWindowClose;
        ListedEmpty.Text = Strings.ListWindowEmpty;

        ListedBox.ItemsSource = _listed;
        RunningBox.ItemsSource = _running;

        RebuildListed();

        Loaded += async (_, _) => await RefreshRunningAsync();
        SourceInitialized += (_, _) => WindowChromeHelper.UseDarkTitleBar(this);
    }

    /// <summary>Whether the list differs from the one the window was opened with.</summary>
    public bool Changed { get; private set; }

    /// <summary>The list as it stands, exact paths.</summary>
    public IReadOnlyList<string> Entries => _entries;

    private void RebuildListed()
    {
        _listed.Clear();

        foreach (var path in _entries)
        {
            var exists = File.Exists(path);

            _listed.Add(new ProgramRow(
                path,
                RunningPrograms.DisplayNameOf(path),
                ProgramIcons.For(path),
                exists ? null : Strings.ListWindowFileMissing,
                Strings.ListWindowRemove,
                actionEnabled: true,
                isListed: true));
        }

        ListedCount.Text = _entries.Count == 0
            ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, Strings.ListCountFormat, _entries.Count);

        ListedEmpty.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ApplySearch();
    }

    private async Task RefreshRunningAsync()
    {
        if (_loadingRunning)
        {
            return;
        }

        _loadingRunning = true;
        RefreshButton.IsEnabled = false;
        RunningEmpty.Text = Strings.ListWindowLoading;
        RunningEmpty.Visibility = Visibility.Visible;

        try
        {
            _allRunning = await Task.Run(RunningPrograms.Enumerate);
        }
        catch (Exception ex)
        {
            Log.Error("Listing the running programs failed", ex);
            _allRunning = [];
        }
        finally
        {
            _loadingRunning = false;
            RefreshButton.IsEnabled = true;
        }

        ApplySearch();
    }

    private void ApplySearch()
    {
        if (_loadingRunning)
        {
            return;
        }

        var query = SearchBox.Text.Trim();
        _running.Clear();

        foreach (var program in _allRunning)
        {
            // Searching the screen by name or folder is only a way to find a row. What gets added is
            // always that row's exact path.
            if (query.Length > 0 &&
                !program.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) &&
                !program.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var listed = IsListed(program.Path);

            _running.Add(new ProgramRow(
                program.Path,
                program.Name,
                ProgramIcons.For(program.Path),
                note: null,
                listed ? Strings.ListWindowAlreadyListed : Strings.ListWindowAdd,
                actionEnabled: !listed,
                isListed: false));
        }

        RunningEmpty.Text = Strings.ListWindowNoMatch;
        RunningEmpty.Visibility = _running.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool IsListed(string path) => _entries.Contains(path, StringComparer.OrdinalIgnoreCase);

    private void Add(string path)
    {
        var canonical = ExecutablePaths.Canonicalize(path);

        if (canonical is null)
        {
            MessageBox.Show(this,
                string.Format(CultureInfo.CurrentCulture, Strings.ListWindowCannotResolve, path),
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (IsListed(canonical))
        {
            return;
        }

        _entries.Add(canonical);
        Changed = true;
        Log.App($"{Title}: added {canonical}");
        RebuildListed();
    }

    private void Remove(string path)
    {
        var removed = _entries.RemoveAll(entry => entry.Equals(path, StringComparison.OrdinalIgnoreCase));

        if (removed == 0)
        {
            return;
        }

        Changed = true;
        Log.App($"{Title}: removed {path}");
        RebuildListed();
    }

    private void OnRowActionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProgramRow row)
        {
            return;
        }

        if (row.IsListed)
        {
            Remove(row.Path);
        }
        else
        {
            Add(row.Path);
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplySearch();

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshRunningAsync();

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Strings.ListWindowFileFilter,
            Multiselect = true,
            CheckFileExists = true,

            // A shortcut is followed to the program it starts, which is what would be matched.
            DereferenceLinks = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        foreach (var file in dialog.FileNames)
        {
            Add(file);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>One row in either list.</summary>
    public sealed class ProgramRow(
        string path,
        string name,
        ImageSource? icon,
        string? note,
        string actionText,
        bool actionEnabled,
        bool isListed)
    {
        public string Path { get; } = path;

        public string Name { get; } = name;

        public ImageSource? Icon { get; } = icon;

        public string? Note { get; } = note;

        public Visibility NoteVisibility => Note is null ? Visibility.Collapsed : Visibility.Visible;

        public string ActionText { get; } = actionText;

        public bool ActionEnabled { get; } = actionEnabled;

        public bool IsListed { get; } = isListed;
    }
}
