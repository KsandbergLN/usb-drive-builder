using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LaptopQaUsbBuilder;

public static class ThemeService
{
    public static readonly string[] Themes = ["Light", "Dark", "AMOLED"];
    public static string Normalize(string? value) => Themes.FirstOrDefault(t => t.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? "Light";

    public static void Apply(DependencyObject root, string? theme)
    {
        var name = Normalize(theme);
        var shell = name == "Light" ? "#F4F5F1" : name == "AMOLED" ? "#000000" : "#30464F";
        var card = name == "Light" ? "#FCFDFB" : name == "AMOLED" ? "#050505" : "#40545C";
        var input = name == "Light" ? "#FBFCFA" : name == "AMOLED" ? "#090909" : "#59666C";
        var text = name == "Light" ? "#17272D" : name == "AMOLED" ? "#F4F4F4" : "#F7FAFB";
        var muted = name == "Light" ? "#526970" : name == "AMOLED" ? "#BDBDBD" : "#D1DADD";
        var stroke = name == "Light" ? "#9BB0B7" : name == "AMOLED" ? "#5A5A5A" : "#82969E";
        if (root is Window window)
        {
            window.Resources["ThemeShell"] = Brush(shell);
            window.Resources["ThemeCard"] = Brush(card);
            window.Resources["ThemeInput"] = Brush(input);
            window.Resources["ThemeText"] = Brush(text);
            window.Resources["ThemeMuted"] = Brush(muted);
            window.Resources["ThemeStroke"] = Brush(stroke);
            window.Resources["ThemeShellStroke"] = Brush(name == "Light" ? "#E0E5E2" : stroke);
            window.Resources["ThemeSelection"] = Brush(name == "Light" ? "#C9F1DD" : name == "AMOLED" ? "#0B3B25" : "#176044");
            window.Resources["ReadyBackground"] = Brush(name == "AMOLED" ? "#0C6B3F" : "#D7F3E5");
            window.Resources["ReadyBorder"] = Brush(name == "AMOLED" ? "#2BE884" : "#74D3A2");
            window.Resources["ReadyText"] = Brush(name == "AMOLED" ? "#E1FFEE" : "#147A4B");
            window.Resources["WarningBackground"] = Brush(name == "AMOLED" ? "#7A1F28" : "#FBE8E7");
            window.Resources["WarningBorder"] = Brush(name == "AMOLED" ? "#FF6B76" : "#E6A7A8");
            window.Resources["WarningText"] = Brush(name == "AMOLED" ? "#FFF0F1" : "#8E292F");
            window.Resources["DriveCardBackground"] = Brush(name == "Light" ? "#EFF3F1" : name == "AMOLED" ? "#090909" : "#52656C");
            window.Resources["DriveCardBorder"] = Brush(name == "Light" ? "#A9BABF" : name == "AMOLED" ? "#666666" : "#91A3AA");
            window.Resources["DriveSelectedBackground"] = Brush(name == "Light" ? "#D7F3E5" : name == "AMOLED" ? "#0B3B25" : "#176044");
            var driveSelectedBorder = Brush(name == "Light" ? "#20B86A" : name == "AMOLED" ? "#35E58A" : "#48D998");
            window.Resources["DriveSelectedBorder"] = driveSelectedBorder;
            window.Resources["DriveActiveBackground"] = CreateAnimatedDiagonalStripeBrush(driveSelectedBorder.Color);
            window.Resources["DriveCompletedBackground"] = Brush(Darken(driveSelectedBorder.Color, 0.58));
            window.Resources["DriveProgressText"] = Brush("#F7FFFB");
            window.Resources["DriveFailedBackground"] = Brush(name == "AMOLED" ? "#9E3039" : name == "Dark" ? "#B94D56" : "#C75E63");
            window.Resources["DriveFailedBorder"] = Brush(name == "AMOLED" ? "#FF7C86" : "#AE3338");
            window.Resources["DriveText"] = Brush(text);
            window.Resources["DriveMutedText"] = Brush(muted);
            window.Resources["DriveHoverBorder"] = Brush(name == "Light" ? "#526970" : name == "AMOLED" ? "#E5E5E5" : "#D5E3E7");
            window.Resources["AddButtonBackground"] = Brush(name == "AMOLED" ? "#0C6B3F" : "#D7F3E5");
            window.Resources["AddButtonForeground"] = Brush(name == "AMOLED" ? "#E1FFEE" : "#147A4B");
            window.Resources["ClearButtonBackground"] = Brush(name == "AMOLED" ? "#7A1F28" : "#D8A2A3");
            window.Resources["ClearButtonForeground"] = Brush(name == "AMOLED" ? "#FFF0F1" : "#8E292F");
            var lightSegments = new[] { "#D8F1E5", "#DCEAF4", "#F4E8CF", "#E8DFF2", "#F2DDDC", "#D8ECEB" };
            var amoledSegments = new[] { "#0B3B25", "#0C3145", "#44340D", "#352342", "#421F22", "#073A37" };
            var segmentBorders = name == "AMOLED"
                ? new[] { "#48D998", "#62BCEB", "#E0B854", "#B590D7", "#DC8587", "#58C8C1" }
                : new[] { "#55C98D", "#6AAED6", "#D5A84E", "#A987C5", "#D3827F", "#62B4AF" };
            var segmentColors = name == "AMOLED" ? amoledSegments : lightSegments;
            window.Resources["PartitionText"] = Brush(name == "AMOLED" ? text : "#17313A");
            for (var i = 0; i < segmentColors.Length; i++)
            {
                window.Resources[$"PartitionBackground{i}"] = Brush(segmentColors[i]);
                window.Resources[$"PartitionBorder{i}"] = Brush(segmentBorders[i]);
            }
        }
        ApplyNode(root, shell, card, input, text, muted, stroke);
    }

    private static void ApplyNode(DependencyObject node, string shell, string card, string input, string text, string muted, string stroke)
    {
        if (node is Window window) window.SetResourceReference(Control.ForegroundProperty, "ThemeText");
        if (node is Border border)
        {
            Replace(border, Border.BackgroundProperty, ["#F4F5F1", "#253640", "#30363B", "#30464F", "#000000"], shell);
            Replace(border, Border.BackgroundProperty, ["#FCFDFB", "#2A414A", "#3A4248", "#40545C", "#050505"], card);
            Replace(border, Border.BorderBrushProperty, ["#9BB0B7", "#9EB1B7", "#668294", "#75828A", "#82969E", "#5A5A5A"], stroke);
            if (border.Name == "MainShell") border.SetResourceReference(Border.BorderBrushProperty, "ThemeShellStroke");
        }
        if (node is Control control)
        {
            Replace(control, Control.ForegroundProperty, ["#17272D", "#F3F7F8", "#F7FAFB", "#F4F4F4"], text);
            Replace(control, Control.BackgroundProperty, ["#FBFCFA", "#1D3038", "#454D53", "#59666C", "#090909"], input);
            Replace(control, Control.BackgroundProperty, ["#FCFDFB", "#2A414A", "#3A4248", "#40545C", "#050505"], card);
            Replace(control, Control.BorderBrushProperty, ["#9BB0B7", "#9EB1B7", "#668294", "#75828A", "#82969E", "#5A5A5A"], stroke);
            if (control is TextBox or ListBox)
                control.SetResourceReference(Control.ForegroundProperty, "ThemeText");
            if (control is ComboBox)
            {
                control.SetResourceReference(Control.ForegroundProperty, "ThemeText");
                control.SetResourceReference(Control.BackgroundProperty, "ThemeInput");
                control.SetResourceReference(Control.BorderBrushProperty, "ThemeStroke");
            }
            if (control is DataGrid or DataGridCell or DataGridColumnHeader)
            {
                control.SetResourceReference(Control.ForegroundProperty, "ThemeText");
                control.SetResourceReference(Control.BackgroundProperty, control is DataGridColumnHeader or DataGrid ? "ThemeCard" : "ThemeInput");
                control.SetResourceReference(Control.BorderBrushProperty, "ThemeStroke");
            }
        }
        if (node is TextBlock block)
        {
            Replace(block, TextBlock.ForegroundProperty, ["#17272D", "#F3F7F8", "#F7FAFB", "#F4F4F4"], text);
            Replace(block, TextBlock.ForegroundProperty, ["#526970", "#61747B", "#687F87", "#40565D", "#B9C7CB", "#C5CDD1", "#D1DADD", "#BDBDBD"], muted);
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) ApplyNode(VisualTreeHelper.GetChild(node, i), shell, card, input, text, muted, stroke);
    }

    private static void Replace(DependencyObject target, DependencyProperty property, string[] known, string replacement)
    {
        if (DependencyPropertyHelper.GetValueSource(target, property).IsExpression) return;
        if (target.GetValue(property) is SolidColorBrush solid && known.Any(k => solid.Color == (Color)ColorConverter.ConvertFromString(k)))
            target.SetValue(property, Brush(replacement));
    }
    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
    private static SolidColorBrush Brush(Color value) => new(value);
    private static Color Darken(Color color, double factor) => Color.FromRgb(
        (byte)Math.Clamp((int)Math.Round(color.R * factor), 0, 255),
        (byte)Math.Clamp((int)Math.Round(color.G * factor), 0, 255),
        (byte)Math.Clamp((int)Math.Round(color.B * factor), 0, 255));
    public static Brush CreateAnimatedDiagonalStripeBrush(Color green)
    {
        const double stripePeriod = 28;
        var dark = Darken(green, 0.50);
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(stripePeriod / 2, stripePeriod / 2),
            SpreadMethod = GradientSpreadMethod.Repeat,
            GradientStops =
            [
                new GradientStop(dark, 0),
                new GradientStop(dark, 0.49),
                new GradientStop(green, 0.50),
                new GradientStop(green, 0.99),
                new GradientStop(dark, 1)
            ]
        };
        var movement = new TranslateTransform();
        brush.Transform = movement;
        movement.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            From = 0,
            To = stripePeriod,
            Duration = TimeSpan.FromMilliseconds(850),
            RepeatBehavior = RepeatBehavior.Forever
        });
        return brush;
    }
}
