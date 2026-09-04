using Avalonia.Data.Converters;

namespace Vaktari.Ui.ViewModels;

public static class PropertiesConverters
{
    /// <summary>
    /// The measure button doubles as a cancel button while running.
    ///
    /// **Both words were lower case, on a window whose other buttons were
    /// not.** Sentence case is the one rule now; LabelCasingTests holds it.
    /// </summary>
    public static readonly IValueConverter MeasureLabel =
        new FuncValueConverter<bool, string>(measuring => measuring ? "Stop" : "Measure");
}
