using System.Windows;

namespace Winora;

public static class AppTheme
{
    public static bool CurrentIsDark { get; private set; }

    public static void Apply(bool dark)
    {
        var application = Application.Current ?? throw new InvalidOperationException("An application is required to change the app theme.");
        application.Dispatcher.VerifyAccess();
        var assembly = typeof(AppTheme).Assembly.GetName().Name;
        var palette = new ResourceDictionary
        {
            Source = new Uri($"/{assembly};component/Assets/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative)
        };
        var dictionaries = application.Resources.MergedDictionaries;
        var previous = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.EndsWith("Assets/Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) == true
            || dictionary.Source?.OriginalString.EndsWith("Assets/Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase) == true);
        if (previous is not null) dictionaries[dictionaries.IndexOf(previous)] = palette;
        else dictionaries.Insert(0, palette);
        CurrentIsDark = dark;
    }
}
