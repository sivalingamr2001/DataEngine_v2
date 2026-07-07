using System.Text.Json;
using DataEngine.Core.Domain;
using DataEngine.Core.Enums;

namespace DataEngine.Core.Tests;

public class PayloadContractTests
{
    [Fact]
    public void DeserializeFetchConfig_AllowsInputParametersAlias()
    {
        var json = """
        {
          "queryNumber": 1,
          "queryKey": "GET_PERFORMANCE_METRICS",
          "queryText": "SELECT id FROM test_table",
          "enableDirectQueryExecution": false,
          "inputParameters": {
            "definitionKey": "KEY_025"
          },
          "count": 50,
          "pageNumber": 1
        }
        """;

        var config = JsonSerializer.Deserialize<FetchConfig>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(config);
        Assert.True(config!.EffectiveParameters.ContainsKey("definitionKey"));
        Assert.Equal("KEY_025", config.EffectiveParameters["definitionKey"]?.ToString());
    }

    [Fact]
    public void DeserializeFetchConfig_ParsesFilterOperatorsFromStrings()
    {
        var json = """
        {
          "filterConditions": [
            {
              "column": "is_active",
              "field": "IsActive",
              "operator": "EQUALS",
              "value": 1
            }
          ]
        }
        """;

        var config = JsonSerializer.Deserialize<FetchConfig>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(config);
        Assert.Single(config!.FilterConditions);
        Assert.Equal(FilterOperator.Equals, config.FilterConditions.Single().Operator);
    }

    [Fact]
    public void DeserializeTransactionRequest_AllowsEmptyTransactionIdString()
    {
        var json = """
        {
          "transactionEntityName": "de_query_definitions",
          "transactionId": "",
          "userId": "admin-user",
          "useModelBinding": true,
          "extendedProperties": {
            "definition_key": "sales-report-query"
          }
        }
        """;

        var request = JsonSerializer.Deserialize<TransactionRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        Assert.Equal(string.Empty, request!.TransactionIdValue?.ToString());
        Assert.Equal(Guid.Empty, request.TransactionId);
    }
}
