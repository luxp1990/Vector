using System;
using System.Windows;

namespace Vector.Services;

public class ThemeService : IThemeService
{
    private bool _isDark = true;
    public bool IsDark => _isDark;

    private ResourceDictionary? _darkDict;
    private ResourceDictionary? _lightDict;

    public ThemeService()
    {
        _darkDict = new ResourceDictionary { Source = new Uri("pack://application:,,,/Themes/Dark.xaml") };
        _lightDict = new ResourceDictionary { Source = new Uri("pack://application:,,,/Themes/Light.xaml") };
    }

    public void Toggle() => Apply(!_isDark);

    public void Apply(bool dark)
    {
        _isDark = dark;
        var app = System.Windows.Application.Current;
        var merged = app.Resources.MergedDictionaries;

        // 找出并移除所有主题字典（Dark.xaml / Light.xaml），保留 Styles.xaml
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            var src = merged[i].Source?.OriginalString ?? "";
            if (src.Contains("Dark") || src.Contains("Light"))
            {
                merged.RemoveAt(i);
            }
        }

        // 把新主题字典插入到 Styles.xaml 之前（位置 0），确保主题优先级正确
        var themeDict = dark ? _darkDict! : _lightDict!;
        merged.Insert(0, themeDict);
    }
}
