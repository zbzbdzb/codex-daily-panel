using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Markup;
using Microsoft.Win32;

namespace CodexUsage.DailyPanel;

internal static class DailyTheme
{
    internal static bool IsDark(ThemePreference preference)
    {
        if (preference != ThemePreference.System) return preference == ThemePreference.Dark;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { return false; }
    }

    internal static void Apply(FrameworkElement target, ThemePreference preference)
    {
        var dark = IsDark(preference);
        var colors = new (string Key, string Hex)[]
        {
            ("Panel.Surface",dark ? "#181818" : "#FFFFFF"), ("Panel.Elevated",dark ? "#282828" : "#F9F9F9"),
            ("Panel.Text",dark ? "#DFDFDF" : "#1A1C1F"), ("Panel.Secondary",dark ? "#B0B0B0" : "#5E6062"),
            ("Panel.Border",dark ? "#414141" : "#E3E3E3"), ("Panel.Accent",dark ? "#8EB5FF" : "#245AC4"),
            ("Panel.Warning",dark ? "#E5C182" : "#80500B"), ("Panel.Good",dark ? "#9ED4B5" : "#246B48")
        };
        foreach (var (key, hex) in colors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); target.Resources[key] = brush;
        }
        target.Resources[SystemColors.WindowBrushKey] = target.Resources["Panel.Surface"];
        target.Resources[SystemColors.WindowTextBrushKey] = target.Resources["Panel.Text"];
        target.Resources[SystemColors.ControlBrushKey] = target.Resources["Panel.Elevated"];
        target.Resources[SystemColors.ControlTextBrushKey] = target.Resources["Panel.Text"];
        target.Resources[SystemColors.HighlightBrushKey] = target.Resources["Panel.Accent"];
        target.Resources[typeof(Button)] = ButtonStyle();
        // Native Aero templates hard-code light gradients. Theme the actual
        // selection, popup and scrolling surfaces, not just unused brush setters.
        var controls=(ResourceDictionary)XamlReader.Parse(ControlStyles);
        foreach(var key in controls.Keys) target.Resources[key]=controls[key];
    }

    private const string ControlStyles = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
 <Style x:Key="Panel.Toggle" TargetType="ToggleButton">
  <Setter Property="Background" Value="{DynamicResource Panel.Elevated}"/><Setter Property="Foreground" Value="{DynamicResource Panel.Text}"/>
  <Setter Property="BorderBrush" Value="{DynamicResource Panel.Border}"/><Setter Property="BorderThickness" Value="1"/>
  <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ToggleButton">
   <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="8,4" CornerRadius="5"><ContentPresenter/></Border>
   <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter Property="BorderBrush" Value="{DynamicResource Panel.Accent}"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter Property="BorderBrush" Value="{DynamicResource Panel.Accent}"/></Trigger></ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType="ComboBoxItem">
  <Setter Property="Foreground" Value="{DynamicResource Panel.Text}"/><Setter Property="Background" Value="{DynamicResource Panel.Surface}"/>
  <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem">
   <Border Background="{TemplateBinding Background}" BorderBrush="Transparent" BorderThickness="1" Padding="8,5" x:Name="ItemBorder"><ContentPresenter/></Border>
   <ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="ItemBorder" Property="BorderBrush" Value="{DynamicResource Panel.Accent}"/><Setter Property="Background" Value="{DynamicResource Panel.Elevated}"/></Trigger><Trigger Property="IsSelected" Value="True"><Setter Property="Background" Value="{DynamicResource Panel.Elevated}"/></Trigger></ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType="ComboBox">
  <Setter Property="Foreground" Value="{DynamicResource Panel.Text}"/><Setter Property="Background" Value="{DynamicResource Panel.Elevated}"/>
  <Setter Property="FontSize" Value="12"/><Setter Property="MinHeight" Value="30"/>
  <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox">
   <Grid>
    <ToggleButton x:Name="SelectionToggle" Style="{StaticResource Panel.Toggle}" Focusable="False" IsChecked="{Binding IsDropDownOpen,RelativeSource={RelativeSource TemplatedParent},Mode=TwoWay}">
     <Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="20"/></Grid.ColumnDefinitions>
      <ContentPresenter Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" VerticalAlignment="Center"/>
      <TextBlock Grid.Column="1" Text="⌄" HorizontalAlignment="Right" Foreground="{DynamicResource Panel.Text}"/>
     </Grid>
    </ToggleButton>
    <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True" Focusable="False">
     <Border MinWidth="{Binding ActualWidth,RelativeSource={RelativeSource TemplatedParent}}" Background="{DynamicResource Panel.Surface}" BorderBrush="{DynamicResource Panel.Border}" BorderThickness="1" CornerRadius="5">
      <ScrollViewer MaxHeight="220" CanContentScroll="True" VerticalScrollBarVisibility="Auto"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer>
     </Border>
    </Popup>
   </Grid>
   <ControlTemplate.Triggers><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="SelectionToggle" Property="BorderBrush" Value="{DynamicResource Panel.Accent}"/></Trigger></ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style x:Key="Panel.ScrollPage" TargetType="RepeatButton"><Setter Property="Focusable" Value="False"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></Setter.Value></Setter></Style>
 <Style TargetType="ScrollBar">
  <Setter Property="Width" Value="12"/><Setter Property="Background" Value="{DynamicResource Panel.Surface}"/>
  <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar">
   <Border Background="{TemplateBinding Background}"><Track x:Name="PART_Track" Orientation="Vertical" IsDirectionReversed="True">
    <Track.DecreaseRepeatButton><RepeatButton Style="{StaticResource Panel.ScrollPage}" Command="ScrollBar.PageUpCommand"/></Track.DecreaseRepeatButton>
    <Track.Thumb><Thumb MinHeight="18"><Thumb.Template><ControlTemplate TargetType="Thumb"><Border Background="{DynamicResource Panel.Secondary}" CornerRadius="4" Margin="3,0"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
    <Track.IncreaseRepeatButton><RepeatButton Style="{StaticResource Panel.ScrollPage}" Command="ScrollBar.PageDownCommand"/></Track.IncreaseRepeatButton>
   </Track></Border>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
</ResourceDictionary>
""";

    private static Style ButtonStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        foreach (var property in new[] { Border.BackgroundProperty, Border.BorderBrushProperty, Border.BorderThicknessProperty, Border.PaddingProperty })
            border.SetBinding(property, new Binding(property.Name) { RelativeSource = RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Button)) { VisualTree = border }));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Panel.Elevated")));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Panel.Text")));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Panel.Border")));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8,4,8,4)));
        style.Setters.Add(new Setter(Control.MinHeightProperty, 32d));
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Panel.Accent")));
        focus.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(2)));
        style.Triggers.Add(focus);
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Panel.Accent")));
        style.Triggers.Add(hover);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .55)); style.Triggers.Add(disabled);
        return style;
    }
}
