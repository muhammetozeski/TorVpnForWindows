using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using TorVpnForWindows.Config;
using TorVpnForWindows.Ui;

namespace TorVpnForWindows.Tests;

/// <summary>
/// Renders one of the program list screens to a PNG file so it can be looked at without starting the
/// application.
///
///     UiSnapshot.exe &lt;output folder&gt; card|editor|empty
///
/// The application's App class is never created. WPF runs an Application's OnStartup as soon as its
/// dispatcher processes anything, and the first version of this tool, which created App to get its
/// resources, ran the real startup: without administrator rights it put up the "needs administrator"
/// message box on every run. A plain Application is created instead, and the styles are read from
/// App.xaml, which is embedded in this tool for the purpose.
///
/// The window is shown far off screen, without activation or a task bar button, only long enough to
/// lay out and render. One screen per run keeps each picture independent of the previous window.
/// </summary>
internal static class Program
{
    private const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>
    /// The application's strings, read by name. The class is internal to the application and stays
    /// that way; this tool reads it rather than asking the application to open it up.
    /// </summary>
    private static string S(string name) =>
        (string)typeof(ProgramListWindow).Assembly.GetType("TorVpnForWindows.Localization.Strings")!
            .GetField(name)!.GetValue(null)!;

    [STAThread]
    private static int Main(string[] args)
    {
        var output = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "UiSnapshot");
        var screen = args.Length > 1 ? args[1] : "card";
        Directory.CreateDirectory(output);

        // A window application has no console to print a failure to, so it goes into a file.
        try
        {
            return Run(output, screen);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, $"error-{screen}.txt"), ex.ToString());
            return 1;
        }
    }

    private static int Run(string output, string screen)
    {
        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
            Resources = LoadApplicationStyles()
        };

        var self = Environment.ProcessPath!;
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var missing = @"C:\Program Files\Removed Program\gone.exe";

        switch (screen)
        {
            case "card":
            {
                var card = new ProgramListsCard { Width = 420 };
                card.Bind(new ProgramLists { Mode = ProgramListMode.Blacklist, Blacklist = [self, explorer] });
                card.ApplyTexts(new ProgramListsCard.CardTexts(
                    S("SettingTunnelLists"),
                    S("SettingTunnelListsHint"),
                    S("ListWindowTunnelWhite"),
                    S("ListWindowTunnelWhiteHint"),
                    S("ListWindowTunnelBlack"),
                    S("ListWindowTunnelBlackHint")));

                var host = new Window
                {
                    Content = new Border { Padding = new Thickness(20), Child = card },
                    Background = (Brush)app.Resources["BackgroundBrush"],
                    SizeToContent = SizeToContent.WidthAndHeight
                };

                Render(host, Path.Combine(output, "card.png"), TimeSpan.FromMilliseconds(500));
                return 0;
            }

            case "editor":
                // A present entry, a system program and one whose file is gone.
                Render(new ProgramListWindow(S("ListWindowTunnelBlack"), S("ListWindowTunnelBlackHint"), [self, explorer, missing]),
                    Path.Combine(output, "editor.png"), TimeSpan.FromSeconds(4));
                return 0;

            case "empty":
                Render(new ProgramListWindow(S("ListWindowInternetWhite"), S("ListWindowInternetWhiteHint"), []),
                    Path.Combine(output, "editor-empty.png"), TimeSpan.FromSeconds(4));
                return 0;

            default:
                File.WriteAllText(Path.Combine(output, $"error-{screen}.txt"), $"unknown screen '{screen}'; use card, editor or empty");
                return 2;
        }
    }

    /// <summary>
    /// The resource dictionary inside App.xaml, parsed on its own. The log converter is left out: it
    /// is the one entry that names a type from the application's own namespace, and none of these
    /// screens uses it.
    /// </summary>
    private static ResourceDictionary LoadApplicationStyles()
    {
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("UiSnapshot.App.xaml")
            ?? throw new InvalidOperationException("App.xaml is not embedded in this tool.");

        var document = XDocument.Load(stream);
        var dictionary = document.Descendants(XName.Get("ResourceDictionary", Presentation)).First();

        var copy = new XElement(dictionary);
        copy.Descendants().Where(element => element.Name.LocalName == "LogSourceToBrushConverter").Remove();

        return (ResourceDictionary)XamlReader.Parse(copy.ToString());
    }

    private static void Render(Window window, string file, TimeSpan settle)
    {
        // Not -32000: Windows reads that position as a minimised window, which is never laid out.
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -12000;
        window.Top = -12000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();

        // Lets Loaded run and the running programs load before the picture is taken.
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = settle };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);

        var content = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(content);
        var width = (int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY);

        // The content has no background of its own where the window supplies it.
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            context.DrawRectangle(window.Background, null, bounds);
            context.DrawRectangle(new VisualBrush(content), null, bounds);
        }

        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(file);
        encoder.Save(stream);
    }
}
