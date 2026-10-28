using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace CouchDeck.UI
{
    public enum FxMode
    {
        None,
        Tile,   // game tile: zoom, lift, glow, light sweep, caption, dim others
        Pop,    // buttons and cards: small zoom + glow
        Row,    // list rows: barely-there zoom
        Cell    // colour table cells: big zoom so the picked colour is obvious
    }

    /// <summary>
    /// All the motion in the overlay. Animations are created in code (not Storyboards in XAML) so
    /// every amount and duration follows the Customize page live: zoom, lift, glow, speed, off.
    /// </summary>
    public static class Motion
    {
        public static Duration Ms(double milliseconds) =>
            new(TimeSpan.FromMilliseconds(Math.Max(1, milliseconds * ThemeManager.SpeedFactor)));

        public static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
        public static readonly IEasingFunction EaseInOut = Frozen(new SineEase { EasingMode = EasingMode.EaseInOut });
        public static readonly IEasingFunction Spring = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 });
        public static readonly IEasingFunction Gentle = Frozen(new QuinticEase { EasingMode = EasingMode.EaseOut });

        private static IEasingFunction Frozen(EasingFunctionBase easing)
        {
            easing.Freeze();
            return easing;
        }

        /// <summary>Animate (or, with animations off, just set) a property.</summary>
        public static void To(IAnimatable target, DependencyProperty property, double to, double ms,
            IEasingFunction? easing = null, double delayMs = 0, double? from = null, Action? completed = null)
        {
            if (!ThemeManager.AnimationsEnabled || ms <= 0)
            {
                target.BeginAnimation(property, null);
                ((DependencyObject)target).SetValue(property, to);
                completed?.Invoke();
                return;
            }

            DoubleAnimation animation = new(to, Ms(ms))
            {
                EasingFunction = easing ?? EaseOut,
                BeginTime = TimeSpan.FromMilliseconds(delayMs * ThemeManager.SpeedFactor)
            };

            if (from.HasValue)
            {
                animation.From = from.Value;
            }

            if (completed != null)
            {
                animation.Completed += (_, _) => completed();
            }

            target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        }

        /// <summary>Scale + translate transforms on an element, created on first use.</summary>
        public static (ScaleTransform scale, TranslateTransform move) Transforms(UIElement element)
        {
            if (element.RenderTransform is TransformGroup group
                && group.Children.Count == 2
                && group.Children[0] is ScaleTransform s
                && group.Children[1] is TranslateTransform t
                && !group.IsFrozen)
            {
                return (s, t);
            }

            ScaleTransform scale = new(1, 1);
            TranslateTransform move = new();
            element.RenderTransform = new TransformGroup { Children = { scale, move } };
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            return (scale, move);
        }

        /// <summary>Fade + slide in, used for tile cascades, pages, toasts.</summary>
        public static void Entrance(UIElement element, double delayMs, double offsetX = 0, double offsetY = 26,
            double fromScale = 1, double ms = 380)
        {
            (ScaleTransform scale, TranslateTransform move) = Transforms(element);

            if (!ThemeManager.AnimationsEnabled)
            {
                element.BeginAnimation(UIElement.OpacityProperty, null);
                element.Opacity = 1;
                move.BeginAnimation(TranslateTransform.XProperty, null);
                move.BeginAnimation(TranslateTransform.YProperty, null);
                move.X = move.Y = 0;
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = scale.ScaleY = 1;
                return;
            }

            element.Opacity = 0;
            To(element, UIElement.OpacityProperty, 1, ms * 0.75, Gentle, delayMs, from: 0);
            if (Math.Abs(offsetX) > 0.1)
            {
                To(move, TranslateTransform.XProperty, 0, ms, Gentle, delayMs, from: offsetX);
            }

            if (Math.Abs(offsetY) > 0.1)
            {
                To(move, TranslateTransform.YProperty, 0, ms, Gentle, delayMs, from: offsetY);
            }

            if (Math.Abs(fromScale - 1) > 0.001)
            {
                To(scale, ScaleTransform.ScaleXProperty, 1, ms, Gentle, delayMs, from: fromScale);
                To(scale, ScaleTransform.ScaleYProperty, 1, ms, Gentle, delayMs, from: fromScale);
            }
        }
    }

    /// <summary>
    /// Attached behaviours used from XAML:
    ///   ui:Fx.Mode="Tile|Pop|Row|Cell"     focus animation for a Button
    ///   ui:Fx.Icon="&#xE768;"              glyph shown by button templates
    ///   ui:Fx.ArtSource="{Binding Cover}"  paints a Border with the image (rounded corners for free),
    ///                                      fading it in when it arrives; ArtFill="True" = always crop-fill
    /// </summary>
    public static class Fx
    {
        public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
            "Mode", typeof(FxMode), typeof(Fx), new PropertyMetadata(FxMode.None, OnModeChanged));

        public static FxMode GetMode(DependencyObject d) => (FxMode)d.GetValue(ModeProperty);
        public static void SetMode(DependencyObject d, FxMode value) => d.SetValue(ModeProperty, value);

        public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
            "Icon", typeof(string), typeof(Fx), new PropertyMetadata(null));

        public static string? GetIcon(DependencyObject d) => (string?)d.GetValue(IconProperty);
        public static void SetIcon(DependencyObject d, string? value) => d.SetValue(IconProperty, value);

        public static readonly DependencyProperty ArtSourceProperty = DependencyProperty.RegisterAttached(
            "ArtSource", typeof(ImageSource), typeof(Fx), new PropertyMetadata(null, OnArtSourceChanged));

        public static ImageSource? GetArtSource(DependencyObject d) => (ImageSource?)d.GetValue(ArtSourceProperty);
        public static void SetArtSource(DependencyObject d, ImageSource? value) => d.SetValue(ArtSourceProperty, value);

        public static readonly DependencyProperty ArtFillProperty = DependencyProperty.RegisterAttached(
            "ArtFill", typeof(bool), typeof(Fx), new PropertyMetadata(false));

        public static bool GetArtFill(DependencyObject d) => (bool)d.GetValue(ArtFillProperty);
        public static void SetArtFill(DependencyObject d, bool value) => d.SetValue(ArtFillProperty, value);

        // =====================================================================

        private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement element)
            {
                return;
            }

            element.IsKeyboardFocusedChanged -= OnFocusChanged;
            element.Loaded -= OnLoaded;

            if ((FxMode)e.NewValue != FxMode.None)
            {
                element.IsKeyboardFocusedChanged += OnFocusChanged;
                element.Loaded += OnLoaded;
            }
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                Apply(element, element.IsKeyboardFocused, instant: true);
            }
        }

        private static void OnFocusChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                Apply(element, (bool)e.NewValue, instant: false);
            }
        }

        /// <summary>Re-applies the resting/focused look, e.g. after zoom or glow settings changed.</summary>
        public static void Refresh(FrameworkElement element) => Apply(element, element.IsKeyboardFocused, instant: true);

        /// <summary>Shows the focused look on an element that can't take focus (the Customize preview tile).</summary>
        public static void ShowFocused(FrameworkElement element) => Apply(element, true, instant: true);

        private static void Apply(FrameworkElement element, bool focused, bool instant)
        {
            FxMode mode = GetMode(element);
            if (mode == FxMode.None)
            {
                return;
            }

            var settings = ThemeManager.Current;
            bool rich = ThemeManager.EffectiveQuality != Config.GraphicsQuality.Performance;
            (ScaleTransform scale, TranslateTransform move) = Motion.Transforms(element);

            double extra = (Math.Clamp(settings.FocusZoom, 100, 140) - 100) / 100.0;
            double zoom = mode switch
            {
                FxMode.Tile => 1 + extra,
                FxMode.Pop => 1 + extra * 0.4,
                FxMode.Cell => 1.3,
                _ => 1 + extra * 0.1
            };
            double lift = mode == FxMode.Tile ? Math.Clamp(settings.FocusLift, 0, 40) : 0;
            double glow = rich ? Math.Clamp(settings.GlowStrength, 0, 100) / 100.0 : 0;
            double dim = mode == FxMode.Tile ? Math.Clamp(settings.DimUnfocused, 0, 80) / 100.0 : 0;

            // Raise the focused element above its neighbours so zoom and glow aren't covered. In an
            // ItemsControl the panel's children are the item containers, not the Button itself.
            Panel.SetZIndex(PanelChild(element), focused ? 10 : 0);

            // Snappy: a short spring in, an even shorter settle out.
            double duration = instant ? 0 : (focused ? 260 : 170);
            IEasingFunction easing = focused ? Motion.Spring : Motion.EaseOut;

            Animate(scale, ScaleTransform.ScaleXProperty, focused ? zoom : 1, duration, easing);
            Animate(scale, ScaleTransform.ScaleYProperty, focused ? zoom : 1, duration, easing);
            Animate(move, TranslateTransform.YProperty, focused ? -lift : 0, duration, easing);

            if (element is not Control control || control.Template == null)
            {
                return;
            }

            control.ApplyTemplate();

            if (control.Template.FindName("PART_Glow", control) is UIElement glowPart)
            {
                double target = focused ? glow : 0;
                if (target > 0)
                {
                    glowPart.Visibility = Visibility.Visible;
                }

                Animate(glowPart, UIElement.OpacityProperty, target, instant ? 0 : 220, Motion.EaseOut, () =>
                {
                    // An invisible effect still costs GPU time: hide it once faded out.
                    if (target <= 0 && !control.IsKeyboardFocused)
                    {
                        glowPart.Visibility = Visibility.Hidden;
                    }
                });
            }

            if (control.Template.FindName("PART_Dim", control) is UIElement dimPart)
            {
                Animate(dimPart, UIElement.OpacityProperty, focused ? 0 : dim, instant ? 0 : 200, Motion.EaseOut);
            }

            if (control.Template.FindName("PART_Caption", control) is UIElement caption)
            {
                bool show = settings.TileTitles == Config.TileTitleMode.Always
                    || (settings.TileTitles == Config.TileTitleMode.OnFocus && focused);

                // Keep it Hidden (not just transparent) when off, so it costs nothing.
                if (show && caption.Visibility == Visibility.Hidden)
                {
                    caption.Visibility = Visibility.Visible;
                }

                Animate(caption, UIElement.OpacityProperty, show ? 1 : 0, instant ? 0 : 220, Motion.EaseOut, () =>
                {
                    if (!show && caption.Visibility == Visibility.Visible)
                    {
                        caption.Visibility = Visibility.Hidden;
                    }
                });

                (_, TranslateTransform captionMove) = Motion.Transforms(caption);
                Animate(captionMove, TranslateTransform.YProperty, show ? 0 : 12, instant ? 0 : 260, Motion.Gentle);
            }

            if (focused && !instant && rich && settings.ShineSweep && ThemeManager.AnimationsEnabled
                && control.Template.FindName("PART_Shine", control) is Border shine)
            {
                Sweep(shine);
            }
        }

        /// <summary>Slides a diagonal light band across the tile by moving its brush, not the element (no clip needed).</summary>
        private static void Sweep(Border shine)
        {
            if (shine.Background is not LinearGradientBrush brush || brush.IsFrozen)
            {
                brush = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 0.35),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.30),
                        new GradientStop(Color.FromArgb(0x38, 255, 255, 255), 0.47),
                        new GradientStop(Color.FromArgb(0x66, 255, 255, 255), 0.50),
                        new GradientStop(Color.FromArgb(0x38, 255, 255, 255), 0.53),
                        new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.70)
                    },
                    RelativeTransform = new TranslateTransform()
                };
                shine.Background = brush;
            }

            TranslateTransform slide = (TranslateTransform)brush.RelativeTransform;
            shine.Visibility = Visibility.Visible;
            Motion.To(slide, TranslateTransform.XProperty, 1.1, 650, Motion.EaseInOut, 60, from: -1.1,
                completed: () => shine.Visibility = Visibility.Hidden);
        }

        private static UIElement PanelChild(UIElement element)
        {
            DependencyObject current = element;
            for (int depth = 0; depth < 4; depth++)
            {
                DependencyObject? parent = VisualTreeHelper.GetParent(current);
                if (parent == null)
                {
                    break;
                }

                if (parent is Panel)
                {
                    return (UIElement)current;
                }

                current = parent;
            }

            return element;
        }

        private static void Animate(IAnimatable target, DependencyProperty property, double to, double ms,
            IEasingFunction easing, Action? completed = null) =>
            Motion.To(target, property, to, ms, easing, completed: completed);

        private static void OnArtSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Border border)
            {
                return;
            }

            if (e.NewValue is not ImageSource source)
            {
                border.Background = null;
                return;
            }

            // Landscape art (some demos/old games) is letterboxed, unless this border is the fill behind it.
            bool landscape = source is BitmapSource bitmap && bitmap.PixelWidth > bitmap.PixelHeight * 1.05;
            ImageBrush brush = new(source)
            {
                Stretch = landscape && !GetArtFill(border) ? Stretch.Uniform : Stretch.UniformToFill,
                AlignmentY = AlignmentY.Center
            };
            brush.Freeze();
            border.Background = brush;

            if (border.IsLoaded)
            {
                Motion.To(border, UIElement.OpacityProperty, 1, 380, Motion.EaseOut, from: 0);
            }
        }
    }

    /// <summary>
    /// Replaces the ScrollViewer's instant jump with an eased glide whenever focus moves to an
    /// element that's off screen (or close to the edge).
    /// </summary>
    public sealed class SmoothScroller
    {
        private readonly ScrollViewer viewer;
        private double target = double.NaN;
        private TimeSpan lastFrame;
        private bool running;

        private SmoothScroller(ScrollViewer viewer)
        {
            this.viewer = viewer;
        }

        /// <summary>Space kept between the focused element and the viewport edge.</summary>
        public double EdgePadding { get; set; } = 60;

        public bool Horizontal { get; set; }

        public static SmoothScroller Attach(ScrollViewer viewer, FrameworkElement content, bool horizontal = false)
        {
            SmoothScroller scroller = new(viewer) { Horizontal = horizontal };

            // Must be handled below the ScrollViewer: its own (class) handler would jump instantly.
            content.RequestBringIntoView += scroller.OnRequestBringIntoView;
            return scroller;
        }

        public void ScrollToStart()
        {
            target = 0;
            StopAndJump(0);
        }

        private void OnRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            if (e.TargetObject is not FrameworkElement element || !element.IsDescendantOf(viewer))
            {
                return;
            }

            e.Handled = true;

            Rect bounds;
            try
            {
                bounds = element.TransformToAncestor(viewer).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            }
            catch (InvalidOperationException)
            {
                return;
            }

            double offset = Horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset;
            double viewport = Horizontal ? viewer.ViewportWidth : viewer.ViewportHeight;
            double extentMax = Horizontal ? viewer.ScrollableWidth : viewer.ScrollableHeight;
            double start = (Horizontal ? bounds.Left : bounds.Top) + offset;
            double end = (Horizontal ? bounds.Right : bounds.Bottom) + offset;

            double current = double.IsNaN(target) ? offset : target;
            double padding = Math.Min(EdgePadding, Math.Max(0, (viewport - (end - start)) / 2));
            double next = current;

            if (start - padding < current)
            {
                next = start - padding;
            }
            else if (end + padding > current + viewport)
            {
                next = end + padding - viewport;
            }

            next = Math.Clamp(next, 0, Math.Max(0, extentMax));
            if (Math.Abs(next - current) < 0.5)
            {
                return;
            }

            target = next;

            if (!ThemeManager.AnimationsEnabled)
            {
                StopAndJump(target);
                return;
            }

            if (!running)
            {
                running = true;
                lastFrame = TimeSpan.Zero;
                CompositionTarget.Rendering += OnFrame;
            }
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            TimeSpan now = ((RenderingEventArgs)e).RenderingTime;
            if (now == lastFrame)
            {
                return; // Rendering can fire twice per frame
            }

            double dt = lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - lastFrame).TotalSeconds, 0, 0.05);
            lastFrame = now;

            double offset = Horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset;
            double stiffness = 18 / Math.Max(0.35, ThemeManager.SpeedFactor);
            double nextOffset = offset + (target - offset) * (1 - Math.Exp(-dt * stiffness));

            if (Math.Abs(target - nextOffset) < 0.75)
            {
                StopAndJump(target);
                return;
            }

            Set(Math.Round(nextOffset)); // whole pixels: no shimmering text while gliding
        }

        private void StopAndJump(double value)
        {
            if (running)
            {
                CompositionTarget.Rendering -= OnFrame;
                running = false;
            }

            Set(value);
        }

        private void Set(double value)
        {
            if (Horizontal)
            {
                viewer.ScrollToHorizontalOffset(value);
            }
            else
            {
                viewer.ScrollToVerticalOffset(value);
            }
        }
    }
}
