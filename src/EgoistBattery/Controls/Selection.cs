using System.Windows;

namespace EgoistBattery.Controls;

/// <summary>Признак «выбрано» для вкладок и сегментов фильтра: типизированное свойство, по которому срабатывают триггеры шаблона.</summary>
internal static class Selection
{
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.RegisterAttached("IsSelected", typeof(bool), typeof(Selection), new FrameworkPropertyMetadata(false));
    public static bool GetIsSelected(DependencyObject d) => (bool)d.GetValue(IsSelectedProperty);
    public static void SetIsSelected(DependencyObject d, bool value) => d.SetValue(IsSelectedProperty, value);
}
