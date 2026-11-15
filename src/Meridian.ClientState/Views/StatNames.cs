using System.Text;
using Meridian.Core.Stats;

namespace Meridian.ClientState.Views;

/// <summary>Display names for stats: "KineticFlux" becomes "Kinetic Flux".</summary>
public static class StatNames
{
    public static string Display(Stat stat) => Spaced(stat.ToString());

    public static string Spaced(string pascal)
    {
        ArgumentNullException.ThrowIfNull(pascal);
        var b = new StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i]) && !char.IsUpper(pascal[i - 1]))
            {
                b.Append(' ');
            }
            b.Append(pascal[i]);
        }
        return b.ToString();
    }
}
