using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Vht.Ui.ViewModels;

namespace Vht.Ui.Views;

public partial class LogView : UserControl
{
    private bool _tailScrollQueued;

    public LogView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is LogViewModel old)
        {
            ((INotifyCollectionChanged)old.Lines).CollectionChanged -= OnLinesChanged;
        }

        if (e.NewValue is LogViewModel fresh)
        {
            ((INotifyCollectionChanged)fresh.Lines).CollectionChanged += OnLinesChanged;
        }
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
        {
            return;
        }

        QueueTailScroll();
    }

    /// <summary>
    /// Asks for "follow the tail" to happen once the current dispatcher operation is over, rather
    /// than doing it inside the collection's notification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ListBox.ScrollIntoView(object)"/> forces a synchronous layout pass
    /// (<c>ItemsControl.OnBringItemIntoView</c> calls <c>UpdateLayout</c>). Called from a
    /// <c>CollectionChanged</c> handler that runs before the <c>ItemsControl</c> has applied the
    /// change - which is the normal case here, because this view subscribes to the collection when
    /// the data context arrives and the item generator subscribes when the binding is applied -
    /// that nested layout pass measures a generator that is still one change behind, and WPF
    /// throws:
    /// </para>
    /// <code>
    /// System.InvalidOperationException: An ItemsControl is inconsistent with its items source.
    ///  ---> Accumulated count 5 is different from actual count 6.
    ///   at ItemContainerGenerator.Verify()
    ///   at VirtualizingStackPanel.MeasureChild(...)
    ///   at ItemsControl.OnBringItemIntoView(ItemInfo info)
    ///   at Vht.Ui.Views.LogView.OnLinesChanged(...)
    /// </code>
    /// <para>
    /// The first log lines after Start arrive while the window is being laid out, so that is the
    /// moment it happened, and the exception came out of
    /// <c>Application.DispatcherUnhandledException</c> and killed the window process.
    /// </para>
    /// <para>
    /// Posting at <see cref="DispatcherPriority.Background"/> also coalesces a burst: the window
    /// log gets a handful of lines per poll, and one scroll after the burst is what a person
    /// watching the tail wants anyway. It is safe from any thread - <c>BeginInvoke</c> marshals -
    /// which keeps a producer that appends off the dispatcher from breaking layout here.
    /// </para>
    /// </remarks>
    private void QueueTailScroll()
    {
        if (_tailScrollQueued)
        {
            return;
        }

        _tailScrollQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ScrollToTail));
    }

    private void ScrollToTail()
    {
        _tailScrollQueued = false;

        // Nothing to follow while the log viewer is closed or the window sits in the notification
        // area: the next line that arrives after it is shown queues another scroll.
        if (!IsLoaded || LogList.Items.Count == 0)
        {
            return;
        }

        // The last item now, not the one that was added: lines are trimmed off the top, so the
        // item captured in the notification may already be gone.
        LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }
}
