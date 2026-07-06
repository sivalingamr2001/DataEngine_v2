using System.Text.Json.Serialization;

namespace DataEngine.ReaderService.Enums;

public enum CacheOption
{
    IMemory,
    Redis,
}

public enum SortDirection
{
    Asc,
    Desc
}

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
    Update,
    Delete
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DatabaseProvider
{
    MySQL, Oracle
}
