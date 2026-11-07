using System;
using System.Globalization;
using Microsoft.Xrm.Sdk;

namespace BrokerForge.Plugin.Translation
{
    /// <summary>Dataverse runtime values to the invariant strings the canonical contract carries (HLD 7.1).</summary>
    internal static class Invariant
    {
        /// <summary>Returns false for values the contract cannot carry (lookups, null and unknown types).</summary>
        public static bool TryFormat(object value, out string text, out string cellType)
        {
            text = null;
            cellType = null;
            switch (value)
            {
                case string s: text = s; cellType = Contracts.CellTypes.String; return true;
                case int i: text = i.ToString(CultureInfo.InvariantCulture); cellType = Contracts.CellTypes.Int; return true;
                case long l: text = l.ToString(CultureInfo.InvariantCulture); cellType = Contracts.CellTypes.Int; return true;
                case decimal m: text = m.ToString(CultureInfo.InvariantCulture); cellType = Contracts.CellTypes.Decimal; return true;
                case double d: text = d.ToString("R", CultureInfo.InvariantCulture); cellType = Contracts.CellTypes.Decimal; return true;
                case Money money: text = money.Value.ToString(CultureInfo.InvariantCulture); cellType = Contracts.CellTypes.Decimal; return true;
                case bool b: text = b ? "true" : "false"; cellType = Contracts.CellTypes.Bool; return true;
                case DateTime dt:
                    var utc = dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                    text = utc.ToString("o", CultureInfo.InvariantCulture);
                    cellType = Contracts.CellTypes.DateTime;
                    return true;
                case Guid g: text = g.ToString("D"); cellType = Contracts.CellTypes.Guid; return true;
                case OptionSetValue o: text = o.Value.ToString(CultureInfo.InvariantCulture); cellType = Contracts.CellTypes.OptionSet; return true;
                default: return false;
            }
        }
    }
}
