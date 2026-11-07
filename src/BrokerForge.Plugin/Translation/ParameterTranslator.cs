using System;
using System.Collections.Generic;
using System.Globalization;
using BrokerForge.Contracts;
using Microsoft.Xrm.Sdk;

namespace BrokerForge.Plugin.Translation
{
    /// <summary>Custom API input parameters to canonical cells, and output cells back to typed output parameters (HLD 7.1, D-18).</summary>
    public static class ParameterTranslator
    {
        /// <summary>One cell per supplied parameter. A null parameter is left out: the host treats it as not supplied.</summary>
        public static List<Cell> ToInputs(ParameterCollection inputs, string correlationId)
        {
            var cells = new List<Cell>();
            foreach (var input in inputs)
            {
                if (input.Value == null)
                    continue;
                string text, type;
                if (!Invariant.TryFormat(input.Value, out text, out type) || type == CellTypes.OptionSet)
                    throw new UnsupportedQueryException(string.Format("Custom API parameter {0} is a {1}, which BrokerForge does not carry; use string, int, decimal, bool, datetime or guid.",
                        input.Key, input.Value.GetType().Name), correlationId);
                cells.Add(new Cell { Col = input.Key, Type = type, Value = text });
            }
            return cells;
        }

        /// <summary>
        /// Sets each output parameter with its Dataverse type. When the platform has already listed the outputs,
        /// a cell for any other name is refused rather than dropped.
        /// </summary>
        public static void SetOutputs(ParameterCollection outputs, List<Cell> cells, string correlationId)
        {
            var declared = outputs.Count > 0 ? new HashSet<string>(outputs.Keys) : null;
            foreach (var cell in cells ?? new List<Cell>())
            {
                if (declared != null && !declared.Contains(cell.Col))
                    throw new MappingException("The host returned output " + cell.Col + ", which this Custom API does not declare.", correlationId);
                outputs[cell.Col] = cell.Value == null ? null : Parse(cell, correlationId);
            }
        }

        private static object Parse(Cell cell, string correlationId)
        {
            var inv = CultureInfo.InvariantCulture;
            int i;
            decimal m;
            DateTime dt;
            Guid g;
            switch (cell.Type)
            {
                case CellTypes.String:
                    return cell.Value;
                case CellTypes.Int:
                    if (int.TryParse(cell.Value, NumberStyles.AllowLeadingSign, inv, out i)) return i;
                    break;
                case CellTypes.Decimal:
                    if (decimal.TryParse(cell.Value, NumberStyles.Number | NumberStyles.AllowExponent, inv, out m)) return m;
                    break;
                case CellTypes.Bool:
                    if (cell.Value == "true") return true;
                    if (cell.Value == "false") return false;
                    break;
                case CellTypes.DateTime:
                    if (DateTime.TryParse(cell.Value, inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt)) return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                    break;
                case CellTypes.Guid:
                    if (Guid.TryParse(cell.Value, out g)) return g;
                    break;
                default:
                    throw new MappingException(string.Format("Output {0} has type {1}, which a Custom API output cannot take.", cell.Col, cell.Type ?? "(none)"), correlationId);
            }
            // The value is not echoed: an output may be sensitive (design principle 4).
            throw new MappingException(string.Format("Output {0} is not a valid {1}.", cell.Col, cell.Type), correlationId);
        }
    }
}
