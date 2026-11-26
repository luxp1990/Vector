using System;
using System.Windows;

namespace Vector.Services;

public class LanguageService : ILanguageService
{
    private string _currentLanguage = "zh-CN";
    public string CurrentLanguage => _currentLanguage;

    public event EventHandler? LanguageChanged;

    private readonly ResourceDictionary _zhCnDict;
    private readonly ResourceDictionary _enUsDict;

    public LanguageService()
    {
        _zhCnDict = new ResourceDictionary { Source = new Uri("pack://application:,,,/Resources/Strings.zh-CN.xaml") };
        _enUsDict = new ResourceDictionary { Source = new Uri("pack://application:,,,/Resources/Strings.en-US.xaml") };
    }

    public void Apply(string language)
    {
        var normalized = string.Equals(language, "en-US", StringComparison.OrdinalIgnoreCase)
            ? "en-US"
            : "zh-CN";

        bool changed = !string.Equals(_currentLanguage, normalized, StringComparison.Ordinal);
        _currentLanguage = normalized;

        var app = System.Windows.Application.Current;
        if (app != null)
        {
            var merged = app.Resources.MergedDictionaries;
            for (int i = merged.Count - 1; i >= 0; i--)
            {
                var src = merged[i].Source?.OriginalString ?? "";
                if (src.Contains("Strings."))
                {
                    merged.RemoveAt(i);
                }
            }

            var dict = normalized == "en-US" ? _enUsDict : _zhCnDict;
            merged.Add(dict);
        }

        if (changed)
        {
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string GetString(string key)
    {
        if (string.IsNullOrEmpty(key)) return key;
        var app = System.Windows.Application.Current;
        if (app?.TryFindResource(key) is string value)
            return value;
        return key;
    }

    public string Format(string key, params object[] args)
    {
        var template = GetString(key);
        return string.Format(template, args);
    }
}
