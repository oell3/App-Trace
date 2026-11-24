using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace AppTrace
{
    public sealed class ListViewSort
    {
        private readonly ListView _listView;

        public ListViewSort(ListView listView)
        {
            _listView = listView;

            _listView.AddHandler(
                GridViewColumnHeader.ClickEvent,
                new RoutedEventHandler(OnColumnHeaderClick));
        }

        public void OnColumnHeaderClick(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not GridViewColumnHeader header ||
                header.Role == GridViewColumnHeaderRole.Padding ||
                header.Column?.DisplayMemberBinding is not Binding binding)
            {
                return;
            }

            string? property = binding.Path?.Path;

            if (string.IsNullOrWhiteSpace(property))
            {
                return;
            }

            if (property == "FormattedSize")
            {
                property = "SizeinMB";
            }

            ListSortDirection direction = ListSortDirection.Ascending;

            if (_listView.Items.SortDescriptions.Count > 0 &&
                _listView.Items.SortDescriptions[0].PropertyName == property &&
                _listView.Items.SortDescriptions[0].Direction ==
                    ListSortDirection.Ascending)
            {
                direction = ListSortDirection.Descending;
            }

            _listView.Items.SortDescriptions.Clear();
            _listView.Items.SortDescriptions.Add(
                new SortDescription(property, direction));
        }
    }
}