using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CouchDeck.Input
{
    /// <summary>
    /// Turns controller presses into focus movement between Buttons (spatial navigation), so the
    /// same XAML works with a controller, a keyboard and a mouse.
    ///
    /// Why not WPF's own MoveFocus: it measures elements by where they are *drawn*, including
    /// RenderTransforms. A focused tile is zoomed and lifted, so its edges overlap its neighbours
    /// and WPF decides the tile to the right "isn't to the right" and jumps to the row above.
    /// This navigator uses where elements are laid out (animations ignored), and moves in rows:
    /// Left/Right stay in the row, Up/Down go to the nearest row and the closest column in it.
    /// </summary>
    public static class UiNavigator
    {
        /// <summary>Moves focus within <paramref name="scope"/>. Returns false at an edge (nothing that way).</summary>
        public static bool Move(FocusNavigationDirection direction, FrameworkElement scope)
        {
            if (Keyboard.FocusedElement is not FrameworkElement focused || !focused.IsDescendantOf(scope))
            {
                FocusFirstIn(scope);
                return true;
            }

            if (Predict(focused, direction, scope) is not FrameworkElement target)
            {
                return false;
            }

            Keyboard.Focus(target);
            target.BringIntoView(); // smooth-scrolled by SmoothScroller
            return true;
        }

        /// <summary>The element focus would move to, or null at an edge. Public for tests.</summary>
        public static FrameworkElement? Predict(FrameworkElement source, FocusNavigationDirection direction, FrameworkElement scope)
        {
            Rect from = LayoutRect(source, scope);
            Point fromCenter = Center(from);
            bool horizontal = direction is FocusNavigationDirection.Left or FocusNavigationDirection.Right;

            FrameworkElement? bestAligned = null;
            double bestAlignedScore = double.MaxValue;
            FrameworkElement? bestAny = null;
            double bestAnyScore = double.MaxValue;

            foreach (Button candidate in Focusables(scope))
            {
                if (ReferenceEquals(candidate, source))
                {
                    continue;
                }

                Rect to = LayoutRect(candidate, scope);
                Point toCenter = Center(to);

                // Must lie in the pressed direction (by centre, with a little slack).
                double along = direction switch
                {
                    FocusNavigationDirection.Right => toCenter.X - fromCenter.X,
                    FocusNavigationDirection.Left => fromCenter.X - toCenter.X,
                    FocusNavigationDirection.Down => toCenter.Y - fromCenter.Y,
                    _ => fromCenter.Y - toCenter.Y
                };
                if (along <= 1)
                {
                    continue;
                }

                // Edge gap along the direction (0 when touching or overlapping).
                double gap = direction switch
                {
                    FocusNavigationDirection.Right => Math.Max(0, to.Left - from.Right),
                    FocusNavigationDirection.Left => Math.Max(0, from.Left - to.Right),
                    FocusNavigationDirection.Down => Math.Max(0, to.Top - from.Bottom),
                    _ => Math.Max(0, from.Top - to.Bottom)
                };

                // Same row (for Left/Right) or same column (for Up/Down)?
                double overlap = horizontal
                    ? Math.Min(from.Bottom, to.Bottom) - Math.Max(from.Top, to.Top)
                    : Math.Min(from.Right, to.Right) - Math.Max(from.Left, to.Left);
                double smaller = horizontal ? Math.Min(from.Height, to.Height) : Math.Min(from.Width, to.Width);
                bool aligned = overlap > smaller * 0.3;

                double across = horizontal ? Math.Abs(toCenter.Y - fromCenter.Y) : Math.Abs(toCenter.X - fromCenter.X);

                if (aligned)
                {
                    double score = gap + across * 0.1;
                    if (score < bestAlignedScore)
                    {
                        bestAlignedScore = score;
                        bestAligned = candidate;
                    }
                }
                else
                {
                    // Up/Down into a shorter row or another section: nearest row first, then nearest column.
                    double score = gap * 4 + across;
                    if (score < bestAnyScore)
                    {
                        bestAnyScore = score;
                        bestAny = candidate;
                    }
                }
            }

            if (bestAligned != null)
            {
                // For Up/Down, an unaligned element in a nearer row beats an aligned one further away.
                if (!horizontal && bestAny != null && GapOf(bestAny) + 1 < GapOf(bestAligned))
                {
                    return bestAny;
                }

                return bestAligned;
            }

            // Left/Right never jump rows: at the end of a row you stay put.
            return horizontal ? null : bestAny;

            double GapOf(FrameworkElement element)
            {
                Rect r = LayoutRect(element, scope);
                return direction == FocusNavigationDirection.Down ? Math.Max(0, r.Top - from.Bottom) : Math.Max(0, from.Top - r.Bottom);
            }
        }

        /// <summary>"Press A": clicks whatever button has focus (runs both Click handlers and Commands).</summary>
        public static void ActivateFocused()
        {
            if (Keyboard.FocusedElement is Button button && button.IsEnabled)
            {
                IInvokeProvider? invoker = new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) as IInvokeProvider;
                invoker?.Invoke();
            }
        }

        /// <summary>Focus the first button inside a panel (used when switching pages or opening the overlay).</summary>
        public static void FocusFirstIn(FrameworkElement container)
        {
            // Dispatch at Loaded priority: newly shown items may not be generated/laid out yet.
            container.Dispatcher.BeginInvoke(() =>
            {
                Button? first = FindFirstButton(container);
                if (first != null)
                {
                    Keyboard.Focus(first);
                    first.BringIntoView();
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>True when keyboard focus is somewhere inside <paramref name="container"/>.</summary>
        public static bool HasFocusInside(FrameworkElement container) => container.IsKeyboardFocusWithin;

        /// <summary>
        /// Position by layout only: sums the visual offsets up to <paramref name="scope"/>, ignoring
        /// every RenderTransform (focus zoom/lift, cascades, page slides). Scrolling is included,
        /// because a ScrollViewer moves its content by offset.
        /// </summary>
        private static Rect LayoutRect(FrameworkElement element, FrameworkElement scope)
        {
            Vector offset = default;
            DependencyObject? current = element;
            while (current is Visual visual && !ReferenceEquals(current, scope))
            {
                offset += VisualTreeHelper.GetOffset(visual);
                current = VisualTreeHelper.GetParent(current);
            }

            return new Rect(new Point(offset.X, offset.Y), element.RenderSize);
        }

        private static Point Center(Rect rect) => new(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

        private static IEnumerable<Button> Focusables(DependencyObject parent)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is UIElement { IsVisible: false })
                {
                    continue; // collapsed pages, hidden bars
                }

                if (child is Button button)
                {
                    if (button.IsEnabled && button.Focusable)
                    {
                        yield return button;
                    }

                    continue; // buttons don't nest
                }

                foreach (Button nested in Focusables(child))
                {
                    yield return nested;
                }
            }
        }

        private static Button? FindFirstButton(DependencyObject parent)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);

                if (child is Button button && button.IsVisible && button.IsEnabled && button.Focusable)
                {
                    return button;
                }

                Button? nested = FindFirstButton(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }
    }
}
