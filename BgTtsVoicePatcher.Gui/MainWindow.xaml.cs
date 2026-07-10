using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using BgTtsVoicePatcher.Gui.ViewModels;

namespace BgTtsVoicePatcher.Gui;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.LogLines.CollectionChanged += LogLines_CollectionChanged;
        };
    }

    private void LogLines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
            return;

        if (LogListBox.Items.Count > 0)
            LogListBox.ScrollIntoView(LogListBox.Items[^1]);
    }
}
