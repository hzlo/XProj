using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ProjectManager.Wpf.Infrastructure;

namespace ProjectManager.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(Window_Loaded));
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Button_Pressed), true);
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Button_Released), true);
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.MouseLeaveEvent, new MouseEventHandler(Button_Released), true);
        base.OnStartup(e);
    }

    private static void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        WindowBackdrop.Apply(window, ThemeManager.IsDark);
        if (!SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        window.Opacity = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        window.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    private static void Button_Pressed(object sender, MouseEventArgs e) => AnimateButton(sender as ButtonBase, 0.97, 70);

    private static void Button_Released(object sender, MouseEventArgs e) => AnimateButton(sender as ButtonBase, 1, 160);

    private static void AnimateButton(ButtonBase? button, double target, int duration)
    {
        if (button?.RenderTransform is not ScaleTransform scale || !SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        if (scale.IsFrozen)
        {
            scale = scale.CloneCurrentValue();
            button.RenderTransform = scale;
        }

        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(duration))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation, HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
