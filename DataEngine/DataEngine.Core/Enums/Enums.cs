using System.Text.Json.Serialization;

namespace DataEngine.Core.Enums;

public enum CacheOption
{
    IMemory,
    Redis
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SortDirection
{
    Asc,
    Desc
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FilterOperator
{
    Equals,
    NotEquals,
    Contains,
    StartsWith,
    EndsWith,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    In,
    IsNull,
    IsNotNull
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuditOperation
{
    Create,
    Insert,
    Update,
    Delete
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DatabaseProvider
{
    MySQL,
    Oracle
}