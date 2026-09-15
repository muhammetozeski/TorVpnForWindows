using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TorVpnForWindows.Config;
using TorVpnForWindows.Localization;

namespace TorVpnForWindows.Ui;

/// <summary>
/// One pair of program lists in the settings: a white list and a black list, each with its own
/// switch and its own entries. Turning one switch on turns the other off; both can be off.
/// </summary>
public partial class ProgramListsCard : UserControl
{
    private ProgramLists? _lists;
    private CardTexts? _texts;

    public ProgramListsCard() => InitializeComponent();

    /// <summary>Raised after the mode or the entries changed. The lists object already holds the change.</summary>
    public event Action? ListsChanged;

    /// <summary>The words this card shows. Kept apart from the lists so a language change can reapply them.</summary>
    public sealed record CardTexts(
        string Title,
        string Hint,
        string WhiteWindowTitle,
        string WhiteWindowHint,
        string BlackWindowTitle,
        string BlackWindowHint);

    public void Bind(ProgramLists lists)
    {
        _lists = lists;
        Render();
    }

    public void ApplyTexts(CardTexts texts)
    {
        _texts = texts;

        TitleText.Text = texts.Title;
        HintText.Text = texts.Hint;
        WhiteLabel.Text = Strings.ListWhite;
        BlackLabel.Text = Strings.ListBlack;
        WhiteEditButton.Content = Strings.ListEdit;
        BlackEditButton.Content = Strings.ListEdit;

        Render();
    }

    private void Render()
    {
        if (_lists is null)
        {
            return;
        }

        WhiteToggle.IsChecked = _lists.Mode == ProgramListMode.Whitelist;
        BlackToggle.IsChecked = _lists.Mode == ProgramListMode.Blacklist;
        WhiteCount.Text = CountText(_lists.Whitelist.Count);
        BlackCount.Text = CountText(_lists.Blacklist.Count);
    }

    private static string CountText(int count) => count == 0
        ? Strings.ListCountNone
        : string.Format(CultureInfo.CurrentCulture, Strings.ListCountFormat, count);

    private void OnWhiteToggleClick(object sender, RoutedEventArgs e) =>
        SetMode(WhiteToggle.IsChecked == true, ProgramListMode.Whitelist);

    private void OnBlackToggleClick(object sender, RoutedEventArgs e) =>
        SetMode(BlackToggle.IsChecked == true, ProgramListMode.Blacklist);

    private void SetMode(bool on, ProgramListMode mode)
    {
        if (_lists is null)
        {
            return;
        }

        var next = on ? mode : _lists.Mode == mode ? ProgramListMode.Off : _lists.Mode;

        if (next == _lists.Mode)
        {
            Render();
            return;
        }

        _lists.Mode = next;
        Render();
        ListsChanged?.Invoke();
    }

    private void OnWhiteEditClick(object sender, RoutedEventArgs e)
    {
        if (_lists is null || _texts is null)
        {
            return;
        }

        var edited = Edit(_texts.WhiteWindowTitle, _texts.WhiteWindowHint, _lists.Whitelist);
        if (edited is not null)
        {
            _lists.Whitelist = edited;
            Render();
            ListsChanged?.Invoke();
        }
    }

    private void OnBlackEditClick(object sender, RoutedEventArgs e)
    {
        if (_lists is null || _texts is null)
        {
            return;
        }

        var edited = Edit(_texts.BlackWindowTitle, _texts.BlackWindowHint, _lists.Blacklist);
        if (edited is not null)
        {
            _lists.Blacklist = edited;
            Render();
            ListsChanged?.Invoke();
        }
    }

    /// <summary>Opens the list editor and returns the new entries, or null when nothing changed.</summary>
    private List<string>? Edit(string title, string hint, IEnumerable<string> entries)
    {
        var window = new ProgramListWindow(title, hint, entries)
        {
            Owner = Window.GetWindow(this)
        };

        window.ShowDialog();

        return window.Changed ? window.Entries.ToList() : null;
    }
}
