using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace BgTtsVoicePatcher.Gui.Models;

/// <summary>
/// An ObservableCollection that can be bulk-replaced with a single change
/// notification. Populating a plain ObservableCollection one item at a time (via
/// repeated .Add() calls) triggers a full DataGrid re-layout on every single item -
/// for the Speaker Review grid's 100k+ rows, that turns a sub-second operation into
/// a UI freeze lasting a minute or more. ReplaceAll bypasses per-item notifications
/// entirely and raises one Reset event at the end instead.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
