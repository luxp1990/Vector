using System;

namespace Vector.Services;

public interface ILanguageService
{
    string CurrentLanguage { get; }
    event EventHandler? LanguageChanged;
    void Apply(string language);
    string GetString(string key);
    string Format(string key, params object[] args);
}
