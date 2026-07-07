namespace DataEngine.Core.Exceptions;

public class DataEngineException : Exception
{
    public DataEngineException(string message) : base(message) { }
    public DataEngineException(string message, Exception inner) : base(message, inner) { }
}

public class SqlValidationException : DataEngineException
{
    public SqlValidationException(string message) : base(message) { }
}

public class ConfigurationException : DataEngineException
{
    public ConfigurationException(string message) : base(message) { }
    public ConfigurationException(string message, Exception inner) : base(message, inner) { }
}

public class QueryExecutionException : DataEngineException
{
    public QueryExecutionException(string message) : base(message) { }
    public QueryExecutionException(string message, Exception inner) : base(message, inner) { }
}