namespace Vector.Services;

public interface IThemeService
{
    bool IsDark { get; }
    void Toggle();
    void Apply(bool dark);
}