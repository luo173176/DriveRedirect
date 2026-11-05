using System.Windows.Controls;
using System.Windows.Input;
using DriveRedirect.Desktop.ViewModels;

namespace DriveRedirect.Desktop.Views;

public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
    }

    private void SearchBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is HistoryViewModel vm)
        {
            vm.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void HistoryGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }
}
