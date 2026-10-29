using System.Globalization;
using TMPro;

namespace Fika.Core;

internal static class FikaTextExtensions
{
    // Old bundle TMP labels can retain their serialized text when the numeric
    // SetText overload is used. Set the complete string through the working
    // text input path, including for labels which are still inactive.
    internal static void SetFormattedText(this TMP_Text label, string format, params object[] values) =>
        label.SetText(string.Format(CultureInfo.InvariantCulture, format, values));
}
