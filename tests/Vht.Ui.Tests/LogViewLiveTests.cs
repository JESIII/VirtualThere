using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Application = System.Windows.Application;
using ListBox = System.Windows.Controls.ListBox;
using Vht.Ui.ControlPlane;
using Vht.Ui.ViewModels;
using Vht.Ui.Views;

namespace Vht.Ui.Tests;

/// <summary>
/// The log viewer, laid out for real: a <see cref="LogView"/> in a shown <see cref="Window"/>,
/// wired to a real <see cref="LogViewModel"/>, with lines arriving at the moment they do in the
/// shipped window - while the layout pass that follows Start is still running.
/// </summary>
/// <remarks>
/// These are the cases that killed <c>vht-server.exe</c> on Start (card t_9e1f5f1e): the tail
/// scroll called <c>ListBox.ScrollIntoView</c> from inside the collection's
/// <c>CollectionChanged</c> notification, which forces a nested layout pass over an item generator
/// that has not yet seen the change, and WPF answers with
/// <c>InvalidOperationException: An ItemsControl is inconsistent with its items source</c>. Through
/// <c>Application.DispatcherUnhandledException</c> that took the whole window process down ~0.3s
/// after the worker bound the control pipe.
/// </remarks>
[Collection(WpfUiCollection.Name)]
public sealed class LogViewLiveTests
{
    /// <summary>
    /// The shipping defect: >5 log lines appended while the window is laying itself out, which is
    /// what a start in flight does. Before the fix this threw out of the appends.
    /// </summary>
    [Fact]
    public void Lines_arriving_during_a_start_in_flight_do_not_break_the_list()
    {
        WpfThread.Run(() =>
        {
            (Window window, LogViewModel log) = OpenLogWindow();

            // The window is already showing the first poll's lines: a log viewer with a tail.
            for (int i = 0; i < 4; i++)
            {
                log.AppendLocal("seed " + i);
            }

            window.UpdateLayout();

            // Now a start is in flight: the poll that follows Start lands while the window is
            // still laying out what that start changed (status pill, device rows, log).
            int appendedDuringLayout = 0;
            window.LayoutUpdated += (_, _) =>
            {
                if (appendedDuringLayout != 0)
                {
                    return;
                }

                appendedDuringLayout = 6;
                for (int i = 0; i < appendedDuringLayout; i++)
                {
                    log.AppendLocal("during layout " + i);
                }
            };

            window.InvalidateMeasure();
            window.UpdateLayout();

            Assert.Equal(10, log.Count);
            Assert.Empty(WpfThread.Failures);
        });
    }

    /// <summary>
    /// The same burst, but appended the way the poll does it - from a dispatcher operation with
    /// the window laid out and dirty - and then pumped, so the deferred scroll actually runs.
    /// </summary>
    [Fact]
    public void A_poll_burst_while_the_window_is_dirty_leaves_the_list_consistent()
    {
        WpfThread.Run(() =>
        {
            (Window window, LogViewModel log) = OpenLogWindow();

            for (int i = 0; i < 4; i++)
            {
                log.AppendLocal("seed " + i);
            }

            window.UpdateLayout();

            window.Width += 40; // a layout pass is now pending
            for (int i = 0; i < 6; i++)
            {
                log.AppendLocal("poll line " + i);
            }

            window.UpdateLayout();
            WpfThread.Pump();

            Assert.Equal(10, log.Count);
            Assert.Equal(10, LogList(window).Items.Count);
            Assert.Empty(WpfThread.Failures);
        });
    }

    /// <summary>
    /// The behaviour the scroll exists for is still there: the view follows the tail once the
    /// burst has been posted, and it does that by scrolling the last item into view.
    /// </summary>
    [Fact]
    public void The_view_still_follows_the_tail()
    {
        WpfThread.Run(() =>
        {
            (Window window, LogViewModel log) = OpenLogWindow();

            for (int i = 0; i < 200; i++)
            {
                log.AppendLocal("line " + i);
            }

            WpfThread.Pump();

            ListBox list = LogList(window);
            Assert.Equal(200, list.Items.Count);

            // Realized only when scrolled to: the panel is virtualizing, and the window is far
            // shorter than 200 rows.
            Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(199));
            Assert.Empty(WpfThread.Failures);
        });
    }

    private static ListBox LogList(Window window)
    {
        var view = (LogView)window.Content;
        return (ListBox)view.FindName("LogList")!;
    }

    private static (Window Window, LogViewModel Log) OpenLogWindow()
    {
        var client = new VhtServiceClient(new ControlPlaneClient("vht-logview-" + Guid.NewGuid().ToString("N")));
        var log = new LogViewModel(client);
        var window = new Window
        {
            Width = 420,
            Height = 320,
            Content = new LogView { DataContext = log },
            ShowActivated = false,
        };

        window.Show();
        return (window, log);
    }
}

/// <summary>
/// Groups the tests that need the shared WPF thread. <c>DisableParallelization</c> keeps them from
/// running beside the rest of the suite: one <see cref="Application"/> and one dispatcher are
/// process-wide.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfUiCollection
{
    internal const string Name = "wpf-ui";
}

/// <summary>
/// One STA thread carrying a real <see cref="Application"/>, its resource dictionary and a running
/// dispatcher, shared by every test that needs a laid-out window.
/// </summary>
internal static class WpfThread
{
    private static readonly Lazy<Dispatcher> Instance = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly ConcurrentQueue<Exception> Reported = new();

    private static Dispatcher Dispatcher => Instance.Value;

    /// <summary>Exceptions the dispatcher caught while pumping - the process-killing kind.</summary>
    internal static System.Collections.Generic.IReadOnlyCollection<Exception> Failures => Reported.ToArray();

    /// <summary>Runs <paramref name="body"/> on the UI thread, and fails on anything it threw.</summary>
    internal static void Run(Action body)
    {
        Exception? failure = null;

        Dispatcher.Invoke(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                // Dispose of every window the body opened, so the next test starts clean.
                foreach (Window window in Application.Current.Windows)
                {
                    window.Close();
                }
            }
        });

        if (failure is not null)
        {
            throw new InvalidOperationException("the window threw: " + failure.Message, failure);
        }
    }

    /// <summary>Lets everything already queued - including the deferred tail scroll - run.</summary>
    internal static void Pump() => Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            // The views use StaticResource for their styles; that dictionary is the app's.
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Vht.Ui;component/Themes/AppResources.xaml", UriKind.Absolute),
            });

            // Anything the dispatcher cannot catch would otherwise take the test process with it.
            application.DispatcherUnhandledException += (_, e) =>
            {
                Reported.Enqueue(e.Exception);
                e.Handled = true;
            };

            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "vht-ui-test-sta",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new InvalidOperationException("the WPF test thread did not start");
        }

        return dispatcher ?? throw new InvalidOperationException("the WPF test thread has no dispatcher");
    }
}
