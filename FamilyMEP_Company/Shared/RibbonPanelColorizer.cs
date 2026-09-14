using System.Collections;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Threading;

namespace FamilyMEP.Ribbon;

internal static class RibbonPanelColorizer
{
    public static void ApplyAfterRibbonBuild()
    {
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(Apply));
    }

    private static void Apply()
    {
        try
        {
            Type? componentManager = Type.GetType(
                "Autodesk.Windows.ComponentManager, AdWindows",
                throwOnError: false);
            object? ribbon = componentManager?
                .GetProperty("Ribbon", BindingFlags.Public | BindingFlags.Static)?
                .GetValue(null);
            if (ribbon is null)
            {
                return;
            }

            foreach (object tab in Items(Read(ribbon, "Tabs")))
            {
                string title = Convert.ToString(Read(tab, "Title")) ?? string.Empty;
                if (!title.Equals("FamilyMEP", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (object panel in Items(Read(tab, "Panels")))
                {
                    object? source = Read(panel, "Source");
                    if (source is null)
                    {
                        continue;
                    }

                    string panelTitle = Convert.ToString(Read(source, "Title")) ?? string.Empty;
                    if (panelTitle.Equals("Family Tools", StringComparison.OrdinalIgnoreCase))
                    {
                        SetTitleBackground(panel, Color.FromRgb(255, 213, 79));
                    }
                    else if (panelTitle.Equals("Drainage Tools", StringComparison.OrdinalIgnoreCase))
                    {
                        SetTitleBackground(panel, Color.FromRgb(242, 107, 94));
                    }
                }
            }
        }
        catch
        {
            // Autodesk.Windows is an internal ribbon surface. A theme or Revit
            // update must never prevent the supported Revit add-in from loading.
        }
    }

    private static object? Read(object instance, string propertyName) =>
        instance.GetType()
            .GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?
            .GetValue(instance);

    private static IEnumerable<object> Items(object? collection)
    {
        if (collection is not IEnumerable enumerable)
        {
            yield break;
        }

        foreach (object? item in enumerable)
        {
            if (item is not null)
            {
                yield return item;
            }
        }
    }

    private static void SetTitleBackground(object ribbonPanel, Color color)
    {
        PropertyInfo? property = ribbonPanel.GetType().GetProperty(
            "CustomPanelTitleBarBackground",
            BindingFlags.Public | BindingFlags.Instance);
        if (property?.CanWrite != true)
        {
            return;
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        property.SetValue(ribbonPanel, brush);
    }
}
