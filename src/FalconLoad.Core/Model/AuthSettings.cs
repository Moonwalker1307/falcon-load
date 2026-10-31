namespace FalconLoad.Core.Model;

public enum AuthType
{
    None,

    /// <summary>The OAuth2 client credentials grant (RFC 6749 section 4.4): a service gets a token with its own id and secret.</summary>
    OAuth2ClientCredentials,
}

/// <summary>How the client id and secret are sent to the token endpoint.</summary>
public enum ClientAuthentication
{
    /// <summary>An <c>Authorization: Basic</c> header, which every OAuth2 server has to accept.</summary>
    BasicHeader,

    /// <summary><c>client_id</c> and <c>client_secret</c> fields in the form body, for servers that ask for it.</summary>
    RequestBody,
}

/// <summary>
/// How a test authenticates its requests. With OAuth2 client credentials, the engine gets a token before the first
/// request, sends it as <c>Authorization: Bearer</c> on every request that doesn't set its own <c>Authorization</c>
/// header, and gets a new one shortly before it expires, so a long run keeps going. The fields may use the selected
/// environment's variables, such as <c>{{clientSecret}}</c>, which is where a secret belongs: a value typed straight
/// in is saved and exported with the test.
/// </summary>
public sealed class AuthSettings
{
    public AuthType Type { get; set; } = AuthType.None;

    /// <summary>The token endpoint, such as <c>https://login.example.com/oauth2/token</c>.</summary>
    public string TokenUrl { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Space-separated scopes to ask for; empty leaves the <c>scope</c> field out.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>The API the token is for, for servers that want an <c>audience</c> field; empty leaves it out.</summary>
    public string Audience { get; set; } = string.Empty;

    public ClientAuthentication ClientAuthentication { get; set; } = ClientAuthentication.BasicHeader;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEnabled => Type != AuthType.None;

    /// <param name="environment">The selected environment's values. Auth fields read only environment variables.</param>
    /// <param name="forRun">True when a token is about to be requested, so a variable nothing supplies is a problem.</param>
    public IEnumerable<string> Validate(IReadOnlyDictionary<string, string>? environment = null, bool forRun = false)
    {
        if (!IsEnabled)
        {
            yield break;
        }

        var probe = TemplateRenderer.RenderForValidation(TokenUrl ?? string.Empty, environment: environment, assumeUnresolvedDefined: !forRun);
        if (!Uri.TryCreate(probe.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            yield return "Auth: the token URL must be an absolute http:// or https:// address.";
        }

        if (string.IsNullOrWhiteSpace(ClientId))
        {
            yield return "Auth: enter the client id.";
        }

        if (forRun)
        {
            var missing = new[] { TokenUrl, ClientId, ClientSecret, Scope, Audience }
                .SelectMany(text => TemplateRenderer.UnresolvedNames(text ?? string.Empty, environment, null, null))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (missing.Count > 0)
            {
                yield return $"Auth: no value for {string.Join(", ", missing.Select(m => "{{" + m + "}}"))}: choose an environment that defines it.";
            }
        }
    }

    /// <summary>The settings with the environment's variables filled in, ready to request a token with.</summary>
    public ResolvedAuth Resolve(IReadOnlyDictionary<string, string>? environment) => new(
        new Uri(TemplateRenderer.ResolveVariables(TokenUrl, environment).Trim(), UriKind.Absolute),
        TemplateRenderer.ResolveVariables(ClientId, environment).Trim(),
        TemplateRenderer.ResolveVariables(ClientSecret ?? string.Empty, environment),
        TemplateRenderer.ResolveVariables(Scope ?? string.Empty, environment).Trim(),
        TemplateRenderer.ResolveVariables(Audience ?? string.Empty, environment).Trim(),
        ClientAuthentication);
}

/// <summary><see cref="AuthSettings"/> with every variable filled in.</summary>
public sealed record ResolvedAuth(Uri TokenUrl, string ClientId, string ClientSecret, string Scope, string Audience, ClientAuthentication ClientAuthentication)
{
    // The secret must never reach logs or the UI through the record's generated ToString.
    public override string ToString() => $"OAuth2 client credentials for {ClientId} at {TokenUrl}";
}
