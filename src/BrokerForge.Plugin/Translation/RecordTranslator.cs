using System;
using System.Collections.Generic;
using System.Globalization;
using BrokerForge.Contracts;
using Microsoft.Xrm.Sdk;

namespace BrokerForge.Plugin.Translation
{
    /// <summary>Entity attributes to canonical cells and back. Types come from the runtime values and the host's cell types.</summary>
    public static class RecordTranslator
    {
        public static List<Cell> ToCells(Entity target, string correlationId)
        {
            var primaryKey = target.LogicalName + "id";
            var cells = new List<Cell>();
            foreach (var attribute in target.Attributes)
            {
                if (attribute.Key == primaryKey)
                    continue;
                if (attribute.Value == null)
                {
                    cells.Add(new Cell { Col = attribute.Key, Type = CellTypes.String, Value = null });
                    continue;
                }
                string text, type;
                if (!Invariant.TryFormat(attribute.Value, out text, out type))
                    throw new UnsupportedQueryException(string.Format("Column {0} holds a {1}, which BrokerForge cannot send to the source.", attribute.Key, attribute.Value.GetType().Name), correlationId);
                cells.Add(new Cell { Col = attribute.Key, Type = type, Value = text });
            }
            return cells;
        }

        /// <summary>Sets both Entity.Id and the primary key attribute; the Web API reads the attribute (phase 0 finding 4).</summary>
        public static Entity ToEntity(string logicalName, Row row)
        {
            var id = Guid.Parse(row.Id);
            var entity = new Entity(logicalName, id);
            entity[logicalName + "id"] = id;
            foreach (var cell in row.Cells ?? new List<Cell>())
                entity[cell.Col] = cell.Value == null ? null : Value(cell);
            return entity;
        }

        private static object Value(Cell cell)
        {
            var inv = CultureInfo.InvariantCulture;
            switch (cell.Type)
            {
                case CellTypes.Int: return int.Parse(cell.Value, NumberStyles.AllowLeadingSign, inv);
                case CellTypes.Decimal: return decimal.Parse(cell.Value, NumberStyles.Number | NumberStyles.AllowExponent, inv);
                case CellTypes.Bool: return cell.Value == "true";
                case CellTypes.DateTime:
                case CellTypes.Date:
                    return DateTime.Parse(cell.Value, inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                case CellTypes.Guid: return Guid.Parse(cell.Value);
                case CellTypes.OptionSet: return new OptionSetValue(int.Parse(cell.Value, NumberStyles.AllowLeadingSign, inv));
                default: return cell.Value;
            }
        }
    }
}
