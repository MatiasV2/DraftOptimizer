using DraftOptimizer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace DraftOptimizer;
public sealed class ProfileChampionConverter : IValueConverter
{
    public Dataset? Catalogue { get; set; }
    public object Convert(object value, Type targetType, object parameter, string language) => value is int id ? "Campeón: " + (Catalogue?.NameOf(id) ?? $"ID {id}") : "";
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
