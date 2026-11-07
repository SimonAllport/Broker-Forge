namespace BrokerForge.Contracts
{
    public static class Messages
    {
        public const string Retrieve = "Retrieve";
        public const string RetrieveMultiple = "RetrieveMultiple";
        public const string Create = "Create";
        public const string Update = "Update";
        public const string Delete = "Delete";
    }

    /// <summary>Canonical filter operators (descriptor spec 5). Null/NotNull are not supported in phase 1.</summary>
    public static class Ops
    {
        public const string Eq = "eq";
        public const string Ne = "ne";
        public const string Gt = "gt";
        public const string Ge = "ge";
        public const string Lt = "lt";
        public const string Le = "le";
        public const string In = "in";
        public const string Like = "like";
    }

    public static class CellTypes
    {
        public const string String = "string";
        public const string Int = "int";
        public const string Decimal = "decimal";
        public const string Bool = "bool";
        public const string DateTime = "datetime";
        public const string Date = "date";
        public const string Guid = "guid";
        public const string OptionSet = "optionset";
    }

    /// <summary>Kinds a descriptor's <c>errors</c> map can raise (descriptor spec 8).</summary>
    public static class SourceErrorKinds
    {
        public const string NotFound = "NotFound";
        public const string Conflict = "Conflict";
        public const string Forbidden = "Forbidden";
        public const string Validation = "Validation";
    }
}
