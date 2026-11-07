using System.Collections.Generic;
using System.Linq;
using BrokerForge.Contracts;
using Microsoft.Xrm.Sdk.Query;

namespace BrokerForge.Plugin.Translation
{
    /// <summary>
    /// QueryExpression to the canonical query. It translates shapes only and decides nothing about the
    /// source: whether a filter is declared is the host's call. Constructs the contract cannot carry
    /// (joins, lookups, null tests, contains) fail here, naming the column and operator.
    /// </summary>
    public static class QueryTranslator
    {
        private const int DefaultPageSize = 50;

        public static CanonicalQuery Translate(QueryExpression q, string correlationId)
        {
            if (q.LinkEntities != null && q.LinkEntities.Count > 0)
                throw new UnsupportedQueryException("Queries that join other tables (link-entity) are not supported on BrokerForge virtual tables.", correlationId);

            // Without the cookie the host cannot know where page N starts; treating it as page 1 would repeat rows.
            if (q.PageInfo != null && q.PageInfo.PageNumber > 1 && string.IsNullOrEmpty(q.PageInfo.PagingCookie))
                throw new UnsupportedQueryException(string.Format("Dataverse asked for page {0} without a paging cookie; BrokerForge virtual tables page forward from page 1 only.", q.PageInfo.PageNumber), correlationId);

            var size = q.PageInfo != null && q.PageInfo.Count > 0 ? q.PageInfo.Count : q.TopCount ?? DefaultPageSize;
            return new CanonicalQuery
            {
                Columns = q.ColumnSet == null || q.ColumnSet.AllColumns ? null : q.ColumnSet.Columns.ToList(),
                Filter = q.Criteria != null && (q.Criteria.Conditions.Count > 0 || q.Criteria.Filters.Count > 0) ? Group(q.Criteria, correlationId) : null,
                OrderBy = q.Orders == null || q.Orders.Count == 0
                    ? null
                    : q.Orders.Select(o => new OrderBy { Col = o.AttributeName, Desc = o.OrderType == OrderType.Descending }).ToList(),
                Page = new PageRequest { Size = size, Token = string.IsNullOrEmpty(q.PageInfo?.PagingCookie) ? null : q.PageInfo.PagingCookie },
            };
        }

        private static FilterGroup Group(FilterExpression f, string correlationId)
        {
            return new FilterGroup
            {
                Type = f.FilterOperator == LogicalOperator.Or ? "or" : "and",
                Conditions = f.Conditions.Select(c => Condition(c, correlationId)).ToList(),
                Groups = f.Filters.Select(child => Group(child, correlationId)).ToList(),
            };
        }

        private static Condition Condition(ConditionExpression c, string correlationId)
        {
            if (!string.IsNullOrEmpty(c.EntityName))
                throw new UnsupportedQueryException(string.Format("Filter on {0}.{1} refers to a joined table, which is not supported.", c.EntityName, c.AttributeName), correlationId);

            string op;
            var values = c.Values.ToList();
            switch (c.Operator)
            {
                case ConditionOperator.Equal: op = Ops.Eq; break;
                case ConditionOperator.NotEqual: op = Ops.Ne; break;
                case ConditionOperator.GreaterThan: op = Ops.Gt; break;
                case ConditionOperator.GreaterEqual: op = Ops.Ge; break;
                case ConditionOperator.LessThan: op = Ops.Lt; break;
                case ConditionOperator.LessEqual: op = Ops.Le; break;
                case ConditionOperator.In: op = Ops.In; break;
                case ConditionOperator.BeginsWith: op = Ops.Like; break;
                case ConditionOperator.Like:
                    // Only "begins with" (one trailing %) has a meaning every source can honour.
                    var pattern = values.Count == 1 ? values[0] as string : null;
                    if (pattern == null || !pattern.EndsWith("%") || pattern.IndexOf('%') != pattern.Length - 1 || pattern.Contains("_"))
                        throw Unsupported(c, "Like is supported only as begins-with (a single trailing %)", correlationId);
                    values = new List<object> { pattern.Substring(0, pattern.Length - 1) };
                    op = Ops.Like;
                    break;
                default:
                    throw Unsupported(c, "the operator has no BrokerForge equivalent", correlationId);
            }

            var texts = new List<string>(values.Count);
            foreach (var value in values)
            {
                string text, type;
                if (value == null || !Invariant.TryFormat(value, out text, out type))
                    throw Unsupported(c, string.Format("a {0} value cannot be sent to the source", value == null ? "null" : value.GetType().Name), correlationId);
                texts.Add(text);
            }
            return new Condition { Col = c.AttributeName, Op = op, Values = texts };
        }

        private static UnsupportedQueryException Unsupported(ConditionExpression c, string why, string correlationId)
        {
            return new UnsupportedQueryException(string.Format("Filter on column {0} with operator {1} is not supported: {2}.", c.AttributeName, c.Operator, why), correlationId);
        }
    }
}
