using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Vht.Ui.ViewModels;

namespace Vht.Ui.Views;

public partial class LogView : UserControl
{
    public LogView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
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
        // Follow the tail, like the reference product's message window.
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is { Count: > 0 } items)
        {
            object last = items[items.Count - 1]!;
            LogList.ScrollIntoView(last);
        }
    }
}
