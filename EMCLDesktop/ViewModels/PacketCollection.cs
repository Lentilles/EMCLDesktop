using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace EMCLDesktop.ViewModels;

/// <summary>Updates a large packet window with a single notification for bulk changes.</summary>
internal sealed class PacketCollection : ObservableCollection<PacketItemViewModel>
{
    public void RemoveFirst(int count)
    {
        if (count <= 0) return;
        if (count < 32)
        {
            for (int i = 0; i < count; i++) RemoveAt(0);
            return;
        }
        var remaining = this.Skip(count).ToArray();
        Items.Clear();
        foreach (var item in remaining) Items.Add(item);
        NotifyReset();
    }

    public void AddRange(IReadOnlyList<PacketItemViewModel> items)
    {
        if (items.Count < 32)
        {
            foreach (var item in items) Add(item);
            return;
        }
        foreach (var item in items) Items.Add(item);
        NotifyReset();
    }

    public void ReplaceAll(IEnumerable<PacketItemViewModel> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        NotifyReset();
    }

    private void NotifyReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
