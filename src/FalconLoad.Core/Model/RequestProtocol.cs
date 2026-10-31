namespace FalconLoad.Core.Model;

/// <summary>How a <see cref="RequestDefinition"/> talks to its server.</summary>
public enum RequestProtocol
{
    /// <summary>A plain HTTP request: method, URL, headers and body.</summary>
    Http,

    /// <summary>
    /// A GraphQL operation sent as an HTTP POST with a JSON body of <c>query</c>, <c>variables</c> and
    /// <c>operationName</c>. A response whose <c>errors</c> array has entries counts as a failed request.
    /// </summary>
    GraphQL,
}

/// <summary>The parts of a GraphQL request besides the query, which is the request's body.</summary>
public sealed class GraphQLOptions
{
    /// <summary>The operation's variables as a JSON object, or empty for none. May contain template variables.</summary>
    public string? Variables { get; set; }

    /// <summary>Which operation to run when the query defines several. Optional.</summary>
    public string? OperationName { get; set; }
}
