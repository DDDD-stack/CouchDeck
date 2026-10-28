using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CouchDeck.UI
{
    public enum SettingKind { Header, Choice, Toggle, Slider, Action }

    /// <summary>
    /// One row on the Customize page. Controller: Left/Right changes the value, A cycles/toggles/runs.
    /// Getter/setter lambdas point straight into AppConfig, so there's no copy to keep in sync.
    /// </summary>
    public class SettingItem : ObservableObject
    {
        public SettingItem(SettingKind kind, string label, string glyph = "", string? description = null)
        {
            Kind = kind;
            Label = label;
            Glyph = glyph;
            Description = description;
        }

        public SettingKind Kind { get; }
        public string Label { get; }
        public string Glyph { get; }
        public string? Description { get; }
        public bool HasDescription => !string.IsNullOrEmpty(Description);

        public virtual string ValueText => "";
        public virtual Brush? Swatch => null;
        public bool HasSwatch => Swatch != null;
        public virtual bool IsOn => false;

        /// <summary>0..1 position for slider bars.</summary>
        public virtual double Fraction => 0;

        /// <summary>Raised after the user changed the value.</summary>
        public Action? Changed { get; set; }

        public virtual void Step(int direction) { }
        public virtual void Activate() => Step(+1);

        public void Refresh()
        {
            Raise(nameof(ValueText));
            Raise(nameof(Swatch));
            Raise(nameof(HasSwatch));
            Raise(nameof(IsOn));
            Raise(nameof(Fraction));
        }

        protected void Commit()
        {
            Refresh();
            Changed?.Invoke();
        }
    }

    public sealed class HeaderSetting : SettingItem
    {
        public HeaderSetting(string label, string glyph) : base(SettingKind.Header, label, glyph) { }
    }

    public sealed class ChoiceOption<T>
    {
        public ChoiceOption(T value, string label, string? swatchHex = null)
        {
            Value = value;
            Label = label;
            SwatchHex = swatchHex;
        }

        public T Value { get; }
        public string Label { get; }
        public string? SwatchHex { get; }
    }

    public sealed class ChoiceSetting<T> : SettingItem
    {
        private readonly Func<IReadOnlyList<ChoiceOption<T>>> options;
        private readonly Func<T> get;
        private readonly Action<T> set;

        public ChoiceSetting(string label, string glyph, Func<IReadOnlyList<ChoiceOption<T>>> options, Func<T> get, Action<T> set, string? description = null)
            : base(SettingKind.Choice, label, glyph, description)
        {
            this.options = options;
            this.get = get;
            this.set = set;
        }

        private int CurrentIndex
        {
            get
            {
                IReadOnlyList<ChoiceOption<T>> list = options();
                T value = get();
                for (int i = 0; i < list.Count; i++)
                {
                    if (EqualityComparer<T>.Default.Equals(list[i].Value, value)
                        || (value is string s && list[i].Value is string o && string.Equals(s, o, StringComparison.OrdinalIgnoreCase)))
                    {
                        return i;
                    }
                }

                return -1;
            }
        }

        public override string ValueText
        {
            get
            {
                int index = CurrentIndex;
                return index >= 0 ? options()[index].Label : get()?.ToString() ?? "";
            }
        }

        public override Brush? Swatch
        {
            get
            {
                int index = CurrentIndex;
                string? hex = index >= 0 ? options()[index].SwatchHex : (get() as string);
                if (hex == null || !hex.StartsWith('#'))
                {
                    return null;
                }

                SolidColorBrush brush = new(ThemeManager.ParseColor(hex, "#808080"));
                brush.Freeze();
                return brush;
            }
        }

        public override void Step(int direction)
        {
            IReadOnlyList<ChoiceOption<T>> list = options();
            if (list.Count == 0)
            {
                return;
            }

            int index = CurrentIndex;
            int next = index < 0 ? 0 : (index + direction + list.Count) % list.Count;
            set(list[next].Value);
            Commit();
        }
    }

    public sealed class ToggleSetting : SettingItem
    {
        private readonly Func<bool> get;
        private readonly Action<bool> set;

        public ToggleSetting(string label, string glyph, Func<bool> get, Action<bool> set, string? description = null)
            : base(SettingKind.Toggle, label, glyph, description)
        {
            this.get = get;
            this.set = set;
        }

        public override bool IsOn => get();
        public override string ValueText => get() ? "On" : "Off";

        public override void Step(int direction)
        {
            set(!get());
            Commit();
        }
    }

    public sealed class SliderSetting : SettingItem
    {
        private readonly Func<int> get;
        private readonly Action<int> set;
        private readonly Func<int, string> format;

        public SliderSetting(string label, string glyph, int min, int max, int step, Func<int> get, Action<int> set,
            Func<int, string>? format = null, string? description = null)
            : base(SettingKind.Slider, label, glyph, description)
        {
            Min = min;
            Max = max;
            StepSize = step;
            this.get = get;
            this.set = set;
            this.format = format ?? (v => v.ToString());
        }

        public int Min { get; }
        public int Max { get; }
        public int StepSize { get; }

        public override string ValueText => format(get());
        public override double Fraction => Max == Min ? 0 : Math.Clamp((get() - Min) / (double)(Max - Min), 0, 1);

        public override void Step(int direction)
        {
            int value = Math.Clamp(get() + direction * StepSize, Min, Max);
            if (value != get())
            {
                set(value);
                Commit();
            }
        }

        /// <summary>A on a slider: jump forward, wrapping to the start at the end.</summary>
        public override void Activate()
        {
            int value = get() >= Max ? Min : Math.Min(Max, get() + StepSize);
            set(value);
            Commit();
        }
    }

    public sealed class ActionSetting : SettingItem
    {
        private readonly Action run;
        private readonly Func<string>? value;
        private readonly Func<Brush?>? swatch;

        public ActionSetting(string label, string glyph, Action run, Func<string>? value = null, string? description = null,
            Func<Brush?>? swatch = null)
            : base(SettingKind.Action, label, glyph, description)
        {
            this.run = run;
            this.value = value;
            this.swatch = swatch;
        }

        public override string ValueText => value?.Invoke() ?? "";
        public override Brush? Swatch => swatch?.Invoke();

        public override void Step(int direction) { } // left/right do nothing on actions
        public override void Activate() => run();
    }

    public sealed class SettingTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? Header { get; set; }
        public DataTemplate? Choice { get; set; }
        public DataTemplate? Toggle { get; set; }
        public DataTemplate? Slider { get; set; }
        public DataTemplate? Action { get; set; }

        public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item is SettingItem setting
            ? setting.Kind switch
            {
                SettingKind.Header => Header,
                SettingKind.Toggle => Toggle,
                SettingKind.Slider => Slider,
                SettingKind.Action => Action,
                _ => Choice
            }
            : null;
    }

    public static class Options
    {
        public static IReadOnlyList<ChoiceOption<TEnum>> Enum<TEnum>(params (TEnum value, string label)[] items) =>
            items.Select(i => new ChoiceOption<TEnum>(i.value, i.label)).ToList();
    }
}
